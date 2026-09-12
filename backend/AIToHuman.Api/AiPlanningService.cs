using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIToHuman.Api;
using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Conversations;

/// <summary>
/// AI 集成的默认值。真正生效的值来自设置提供者（数据库覆盖 → 环境变量/配置文件 → 这里的默认值），
/// 因此运营后台改完密钥或模型后，下一轮对话就会用新值，不需要重启进程。
/// </summary>
public sealed class VolcengineAiOptions
{
    public const string DefaultBaseUrl = "https://ark.cn-beijing.volces.com/api/v3";
    public const string DefaultModel = "glm-4-7-251222";
    public const int DefaultTimeoutSeconds = 120;

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = DefaultModel;
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}

public sealed class AiPlanningTimeoutException(int timeoutSeconds, Exception innerException)
    : TimeoutException($"AI 规划服务连续 {timeoutSeconds} 秒没有返回数据。", innerException);

public sealed class AiPlanningUnavailableException(Exception innerException)
    : Exception("无法连接 AI 规划服务。", innerException);

public sealed class AiPlanningUpstreamException(string message)
    : Exception(message);

/// <summary>
/// 模型输出没有通过严格校验。<see cref="VisibleLength"/> 记录本轮已经发给用户的可见字数：
/// 大于零说明页面上已经有了半截回复，此时重试会让用户看到两段拼接内容，因此不能重试。
/// </summary>
public sealed class AiPlanningFormatException(string message, int visibleLength, Exception? innerException = null)
    : Exception(message, innerException)
{
    public int VisibleLength { get; } = visibleLength;
}

public abstract record AiPlanningStreamEvent;
public sealed record AiPlanningDeltaEvent(string Text) : AiPlanningStreamEvent;
public sealed record AiPlanningCompletedEvent(AiConversationTurnResponse Turn) : AiPlanningStreamEvent;

/// <summary>重试前通知页面丢弃已经显示的这一轮回复，避免两段内容拼接。</summary>
public sealed record AiPlanningRestartEvent(string Reason) : AiPlanningStreamEvent;

public sealed class AiPlanningService(HttpClient httpClient, ISettingsProvider settingsProvider, TimeProvider timeProvider, ILogger<AiPlanningService> logger)
{
    /// <summary>把运营配置解析成这一轮要用的值；每次都重新读，热更新立即生效。</summary>
    private VolcengineAiOptions ResolveSettings() => new()
    {
        BaseUrl = settingsProvider.GetValue(SettingKeys.AiBaseUrl) ?? VolcengineAiOptions.DefaultBaseUrl,
        ApiKey = settingsProvider.GetValue(SettingKeys.AiApiKey) ?? string.Empty,
        Model = settingsProvider.GetValue(SettingKeys.AiModel) ?? VolcengineAiOptions.DefaultModel,
        TimeoutSeconds = settingsProvider.GetInt(SettingKeys.AiInactivityTimeoutSeconds) ?? VolcengineAiOptions.DefaultTimeoutSeconds
    };

    private string CurrentModel => settingsProvider.GetValue(SettingKeys.AiModel) ?? VolcengineAiOptions.DefaultModel;

    /// <summary>产品面向国内用户，模型未带时区偏移时按北京时间解释，避免服务器时区影响截止时间。</summary>
    private static readonly TimeSpan ChinaStandardOffset = TimeSpan.FromHours(8);

    /// <summary>模型输出未通过校验时的最大尝试次数。</summary>
    private const int MaxAttempts = 2;

    /// <summary>
    /// 多轮澄清的流式入口。模型输出没有通过严格校验时会重试一次，并把失败原因作为修复指令追加到对话里；
    /// 若这一轮已经把文字发给页面，会先发一个 restart 事件让页面清空，再把重试结果流出去。
    /// 超时和上游不可用不重试，直接交给页面处理。
    /// </summary>
    public async IAsyncEnumerable<AiPlanningStreamEvent> PlanStreamAsync(
        IReadOnlyList<AiConversationMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var attemptMessages = messages;

        for (var attempt = 1; ; attempt++)
        {
            var retryFailure = default(AiPlanningFormatException);

            var enumerator = StreamOnceAsync(attemptMessages, cancellationToken).GetAsyncEnumerator(cancellationToken);
            await using (enumerator.ConfigureAwait(false))
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync();
                    }
                    catch (AiPlanningFormatException exception) when (attempt < MaxAttempts)
                    {
                        retryFailure = exception;
                        break;
                    }

                    if (!moved) break;
                    yield return enumerator.Current;
                }
            }

            if (retryFailure is null) yield break;

            LogRetry(attempt + 1, retryFailure);

            // 已经输出过文字时，先让页面清掉这半截回复，否则重试后两段内容会拼在一起。
            if (retryFailure.VisibleLength > 0)
                yield return new AiPlanningRestartEvent(retryFailure.Message);

            attemptMessages = BuildRepairMessages(messages, retryFailure);
        }
    }

    private async IAsyncEnumerable<AiPlanningStreamEvent> StreamOnceAsync(
        IReadOnlyList<AiConversationMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = ResolveSettings();
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new InvalidOperationException("AI 服务尚未配置 API Key：请在运营后台设置 ai.apiKey，或配置环境变量 VolcengineAI__ApiKey。");

        var conversation = ValidateConversation(messages);
        var now = timeProvider.GetUtcNow();
        var payloadMessages = new List<object> { new { role = "system", content = BuildSystemPrompt(now) } };
        payloadMessages.AddRange(conversation.Select(item => (object)new { role = item.Role, content = item.Content }));

        var payloadObject = new
        {
            model = settings.Model,
            temperature = 0.25,
            max_tokens = 1400,
            stream = true,
            messages = payloadMessages
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(payloadObject)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var timeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 5, 120);
        using var inactivityTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivityTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(message, inactivityTimeout, timeoutSeconds, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(inactivityTimeout.Token);
                throw new AiPlanningUpstreamException(GetUpstreamError(responseText, (int)response.StatusCode));
            }

            var responseStream = await ReadStreamAsync(response, inactivityTimeout, timeoutSeconds, cancellationToken);
            await using var ownedResponseStream = responseStream;
            using var reader = new StreamReader(responseStream);
            var content = new StringBuilder();
            var visibleLength = 0;

            while (true)
            {
                var line = await ReadLineAsync(reader, inactivityTimeout, timeoutSeconds, cancellationToken);
                if (line is null) break;

                inactivityTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var data = line[5..].TrimStart();
                if (data.Length == 0) continue;
                if (data == "[DONE]") break;

                var delta = ParseDelta(data);
                if (delta.Length == 0) continue;
                content.Append(delta);

                // Only the conversational field is exposed. The task state remains server-side until complete.
                var visibleReply = ExtractPartialJsonString(content.ToString(), "assistantMessage");
                if (visibleReply.Length > visibleLength)
                {
                    var newText = visibleReply[visibleLength..];
                    visibleLength = visibleReply.Length;
                    yield return new AiPlanningDeltaEvent(newText);
                }
            }

            if (content.Length == 0)
                throw new AiPlanningUpstreamException("AI 服务结束了响应，但没有返回对话内容。");

            var turn = ParseTurn(content.ToString(), now, visibleLength);
            if (turn.AssistantMessage.Length > visibleLength)
                yield return new AiPlanningDeltaEvent(turn.AssistantMessage[visibleLength..]);
            yield return new AiPlanningCompletedEvent(turn);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage message,
        CancellationTokenSource inactivityTimeout,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, inactivityTimeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && inactivityTimeout.IsCancellationRequested)
        {
            LogTimeout(timeoutSeconds, exception);
            throw new AiPlanningTimeoutException(timeoutSeconds, exception);
        }
        catch (HttpRequestException exception)
        {
            LogUnavailable(exception);
            throw new AiPlanningUnavailableException(exception);
        }
    }

    private async Task<Stream> ReadStreamAsync(
        HttpResponseMessage response,
        CancellationTokenSource inactivityTimeout,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStreamAsync(inactivityTimeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && inactivityTimeout.IsCancellationRequested)
        {
            LogTimeout(timeoutSeconds, exception);
            throw new AiPlanningTimeoutException(timeoutSeconds, exception);
        }
        catch (HttpRequestException exception)
        {
            LogUnavailable(exception);
            throw new AiPlanningUnavailableException(exception);
        }
    }

    private async Task<string?> ReadLineAsync(
        StreamReader reader,
        CancellationTokenSource inactivityTimeout,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadLineAsync(inactivityTimeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && inactivityTimeout.IsCancellationRequested)
        {
            LogTimeout(timeoutSeconds, exception);
            throw new AiPlanningTimeoutException(timeoutSeconds, exception);
        }
        catch (IOException exception)
        {
            LogUnavailable(exception);
            throw new AiPlanningUnavailableException(exception);
        }
    }

    private static IReadOnlyList<AiConversationMessage> ValidateConversation(IReadOnlyList<AiConversationMessage>? messages)
    {
        if (messages is null || messages.Count == 0)
            throw new InvalidOperationException("请先告诉 AI 你想完成什么。");
        if (messages.Count > 30)
            throw new InvalidOperationException("本次对话过长，请生成草稿或重新开始。");
        if (!string.Equals(messages[^1].Role, "user", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("最后一条对话必须来自用户。");

        return messages.Select(item =>
        {
            var role = item.Role.Trim().ToLowerInvariant();
            var content = item.Content.Trim();
            if (role is not ("user" or "assistant"))
                throw new InvalidOperationException("对话角色不正确。");
            if (content.Length is < 1 or > 4000)
                throw new InvalidOperationException("每条对话内容应为 1 至 4000 个字符。");
            return new AiConversationMessage(role, content);
        }).ToArray();
    }

    private void LogTimeout(int timeoutSeconds, Exception exception) =>
        logger.LogWarning(exception, "AI 对话流连续 {TimeoutSeconds} 秒未返回数据，模型为 {Model}。", timeoutSeconds, CurrentModel);

    private void LogUnavailable(Exception exception) =>
        logger.LogWarning(exception, "无法连接 AI 对话服务，模型为 {Model}。", CurrentModel);

    private void LogRetry(int attempt, AiPlanningFormatException failure) =>
        logger.LogWarning("AI 输出未通过校验，准备第 {Attempt} 次尝试：{Reason}", attempt, failure.Message);

    private static string ParseDelta(string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new AiPlanningUpstreamException(GetErrorMessage(error) ?? "AI 服务返回了未知错误。");
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            return string.Empty;

        var choice = choices[0];
        if (!choice.TryGetProperty("delta", out var delta) ||
            !delta.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            return string.Empty;
        return content.GetString() ?? string.Empty;
    }

    private static string ExtractPartialJsonString(string json, string propertyName)
    {
        var propertyIndex = json.IndexOf($"\"{propertyName}\"", StringComparison.Ordinal);
        if (propertyIndex < 0) return string.Empty;
        var colonIndex = json.IndexOf(':', propertyIndex + propertyName.Length + 2);
        if (colonIndex < 0) return string.Empty;
        var quoteIndex = json.IndexOf('"', colonIndex + 1);
        if (quoteIndex < 0) return string.Empty;

        var result = new StringBuilder();
        for (var index = quoteIndex + 1; index < json.Length; index++)
        {
            var character = json[index];
            if (character == '"') break;
            if (character != '\\')
            {
                result.Append(character);
                continue;
            }

            if (++index >= json.Length) break;
            var escaped = json[index];
            if (escaped == 'u')
            {
                if (index + 4 >= json.Length) break;
                var hex = json.Substring(index + 1, 4);
                if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codeUnit)) break;
                result.Append((char)codeUnit);
                index += 4;
                continue;
            }
            result.Append(escaped switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => escaped
            });
        }
        return result.ToString();
    }

    private static AiConversationTurnResponse ParseTurn(string content, DateTimeOffset now, int visibleLength)
    {
        var json = StripCodeFence(content);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var assistantMessage = ReadString(root, "assistantMessage").Trim();
            var readyToDraft = root.TryGetProperty("readyToDraft", out var ready) && ready.ValueKind == JsonValueKind.True;
            AiTaskPlanResponse? plan = null;
            if (readyToDraft && root.TryGetProperty("plan", out var planElement) && planElement.ValueKind == JsonValueKind.Object)
            {
                plan = ParsePlan(planElement, now);
            }

            // 字段缺失不再直接抛 KeyNotFoundException，而是交给校验器给出可展示的原因并触发重试。
            return AiTaskPlanValidator.Validate(new AiConversationTurnResponse(assistantMessage, readyToDraft, plan), visibleLength);
        }
        catch (JsonException exception)
        {
            throw new AiPlanningFormatException("AI 返回的内容不是合法的任务草稿，请重试。", visibleLength, exception);
        }
    }

    /// <summary>模型输出未通过校验时，把失败原因作为修复指令追加到对话尾部，再尝试一次。</summary>
    private static IReadOnlyList<AiConversationMessage> BuildRepairMessages(IReadOnlyList<AiConversationMessage> messages, AiPlanningFormatException failure)
    {
        var repaired = new List<AiConversationMessage>(messages)
        {
            new("user", $"上一次输出没有通过校验：{failure.Message} 请重新只输出一个 JSON 对象，字段顺序为 assistantMessage、readyToDraft、plan；不要输出 Markdown、代码围栏或任何解释。")
        };

        while (repaired.Count > ConversationLimits.MaxHistoryMessages)
        {
            repaired.RemoveAt(0);
        }

        return repaired;
    }

    private static string StripCodeFence(string content)
    {
        var json = content.Trim();
        if (!json.StartsWith("```", StringComparison.Ordinal)) return json;
        var firstLine = json.IndexOf('\n');
        var lastFence = json.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine ? json[(firstLine + 1)..lastFence].Trim() : json;
    }

    private static AiTaskPlanResponse ParsePlan(JsonElement root, DateTimeOffset now) =>
        new(
            ReadString(root, "title"),
            ReadString(root, "description"),
            ReadString(root, "district"),
            ParseDeadline(ReadString(root, "deadline"), now),
            ReadStringArray(root, "acceptanceCriteria"),
            root.TryGetProperty("suggestedReward", out var reward) && reward.TryGetDecimal(out var amount) ? amount : 0,
            ReadStringArray(root, "clarifications"),
            "volcengine");

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string[] ReadStringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : [];

    private static string GetUpstreamError(string responseText, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(responseText);
            if (document.RootElement.TryGetProperty("error", out var error) && GetErrorMessage(error) is { } message)
                return $"AI 服务请求失败（{statusCode}）：{message}";
        }
        catch (JsonException)
        {
            // Upstream error bodies are not guaranteed to be JSON.
        }
        return $"AI 服务请求失败（{statusCode}）。";
    }

    private static string? GetErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return LimitMessage(error.GetString());
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
            return LimitMessage(message.GetString());
        return null;
    }

    private static string? LimitMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var normalized = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    /// <summary>
    /// 解析模型返回的截止时间。模型可能漏掉时区，也可能输出已经过去的日期（例如训练数据里的年份），
    /// 两者都会让“截止时间必须晚于创建时间”在创建任务时失败，因此这里统一纠正。
    /// </summary>
    private static DateTimeOffset ParseDeadline(string? value, DateTimeOffset now)
    {
        var fallback = now.AddDays(1);
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        var trimmed = value.Trim();
        if (!HasExplicitOffset(trimmed))
        {
            // 模型未带时区偏移时按北京时间解释，避免结果随服务器时区变化。
            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                var beijing = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), ChinaStandardOffset);
                return beijing > now ? beijing : fallback;
            }

            return fallback;
        }

        if (!DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return fallback;

        return parsed > now ? parsed : fallback;
    }

    private static bool HasExplicitOffset(string value)
    {
        if (value.EndsWith('Z') || value.EndsWith('z')) return true;
        var timeStart = value.IndexOf('T');
        if (timeStart < 0) return false;
        return value.IndexOf('+', timeStart) >= 0 || value.IndexOf('-', timeStart) >= 0;
    }

    private static string BuildSystemPrompt(DateTimeOffset now)
    {
        var beijing = now.ToOffset(ChinaStandardOffset);
        return $$"""
你是 AIToHuman 的需求澄清助手。你的任务不是立刻展示任务表单，而是通过自然、简短的多轮对话，帮助用户逐步弄清自己真正需要什么。

时间基准（每轮都必须以这里给出的时间为唯一时间来源，不要使用训练数据里的日期）：
- 北京时间（UTC+8）：{{beijing:yyyy-MM-dd HH:mm}}，星期{{WeekdayText(beijing)}}
- UTC：{{now.UtcDateTime:yyyy-MM-dd HH:mm}}

每轮必须只输出一个 JSON 对象，不要输出 Markdown 或代码围栏，并严格按以下字段顺序：
{
  "assistantMessage": "给用户看的自然语言回复",
  "readyToDraft": false,
  "plan": null
}

对话规则：
1. 每次先用一句话回应用户刚提供的信息，再只追问一个当前最关键的问题。不要一次抛出问题清单。
2. 问题必须具体、容易回答；遇到用户可能不了解的概念时给 2 至 3 个简短例子或选择。
3. 不要向用户展示字段名、JSON、内部分析、完成度、信息清单或“我正在梳理”等系统过程。
4. 不要替用户编造地址、联系人、金额、时间、资质或承诺。涉及违法、危险或明显不适合线下服务者执行的请求时，要清楚说明边界并引导到安全方案。
5. 综合完整对话判断是否已经具备：明确目标、执行地点或线上方式、时间要求、可验证的完成标准，以及执行所需的重要限制。缺少关键信息时 readyToDraft 必须为 false，plan 必须为 null。
6. 信息足够时，assistantMessage 用自然语言说明已经可以生成草稿并请用户检查；readyToDraft 为 true，plan 填写完整结构：title（不超过80字）、description、district、deadline（ISO 8601）、acceptanceCriteria（2至6条）、suggestedReward（人民币整数）、clarifications（应为空数组）。线上任务的 district 写“线上”。
7. 用户明确表示不确定时，帮助其做决定，不要机械重复同一个问题。悬赏由 AI 给出建议，服务者不竞价，用户之后只能主动加价。

时间规则：
1. “今天”“明天”“后天”“本周”“下周”“下午”“尽快”等相对说法，必须基于上面的北京时间换算，不要凭印象猜日期。
2. deadline 必须是严格晚于当前时间的未来时刻，用 ISO 8601 带时区偏移表示，例如 2026-09-12T15:00:00+08:00；涉及北京时间时统一使用 +08:00。
3. 用户只表示“尽快”“随时”或没有给出明确时间时，把 deadline 设为当前时间之后 1 至 3 天内的合理时刻。
4. 绝对不要输出已经过去的日期，也不要把训练数据中的年份当作当前年份。
""";
    }

    private static string WeekdayText(DateTimeOffset value) => value.DayOfWeek switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };
}
