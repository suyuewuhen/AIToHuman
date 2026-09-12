using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Tasks;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 替代火山引擎 Ark 的上游替身：记录请求，并按预设返回响应。
/// 用于在不联网的情况下验证 AI 多轮协议的解析与失败路径。
/// </summary>
internal sealed class StubUpstreamHandler(Func<CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<string> RequestBodies { get; } = [];
    public string? AuthorizationHeader { get; private set; }
    public IReadOnlyList<string> AcceptHeaders { get; private set; } = [];
    public string? RequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 请求消息随后会被服务释放，因此在这里把需要断言的内容取出来。
        AuthorizationHeader = request.Headers.Authorization?.ToString();
        AcceptHeaders = request.Headers.Accept.Select(item => item.MediaType ?? string.Empty).ToArray();
        RequestUri = request.RequestUri?.ToString();
        if (request.Content is not null)
        {
            RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
        }

        return responder(cancellationToken);
    }
}

/// <summary>构造 OpenAI 兼容的 SSE 响应体。</summary>
internal static class Sse
{
    /// <summary>一个 chat.completion.chunk，content 为模型输出的片段。</summary>
    public static string ChatChunk(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content } } } });

    /// <summary>把模型输出切成若干片段，每片一个 SSE 事件。<paramref name="terminateWithDone"/> 控制是否追加 [DONE]。</summary>
    public static string Body(IEnumerable<string> pieces, bool terminateWithDone = true)
    {
        var builder = new StringBuilder();
        foreach (var piece in pieces)
        {
            builder.Append("data: ").Append(ChatChunk(piece)).Append("\n\n");
        }

        if (terminateWithDone) builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    /// <summary>直接给出 data 行内容，用于构造错误或畸形载荷。</summary>
    public static string RawBody(params string[] dataLines)
    {
        var builder = new StringBuilder();
        foreach (var line in dataLines)
        {
            builder.Append("data: ").Append(line).Append("\n\n");
        }

        return builder.ToString();
    }

    public static HttpResponseMessage Respond(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    /// <summary>返回响应头但正文永不产出数据，用于验证无活动超时。</summary>
    public static HttpResponseMessage Stall() =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };

    public static IEnumerable<string> Split(string value, int size)
    {
        for (var index = 0; index < value.Length; index += size)
        {
            yield return value.Substring(index, Math.Min(size, value.Length - index));
        }
    }
}

/// <summary>读取时永久阻塞的流；只有取消令牌触发时才结束。</summary>
internal sealed class StallingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>可推进的时钟，用于让 UpdatedAt 之类的排序字段可确定。</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;

    public override DateTimeOffset GetUtcNow() => current;

    public void Advance(TimeSpan delta) => current = current.Add(delta);
}

/// <summary>内存文件存储替身：只保留字节，便于断言"被拒绝的文件没有留下"。</summary>
internal sealed class InMemoryFileStorage : AIToHuman.Application.Orders.IFileStorage
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        Files[key] = buffer.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Files.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) as Stream : null);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Files.ContainsKey(key));

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Files.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>固定返回指定扫描结果的替身，用于验证"未通过扫描不落库"。</summary>
internal sealed class FakeEvidenceScanner(AIToHuman.Domain.Orders.EvidenceScanStatus status) : AIToHuman.Application.Orders.IEvidenceScanner
{
    public int Calls { get; private set; }

    public Task<AIToHuman.Domain.Orders.EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(status);
    }
}

/// <summary>结果可切换、可失败的扫描替身，用于验证“先待扫描、重扫后得出结论”。</summary>
internal sealed class MutableEvidenceScanner(AIToHuman.Domain.Orders.EvidenceScanStatus status = AIToHuman.Domain.Orders.EvidenceScanStatus.Pending)
    : AIToHuman.Application.Orders.IEvidenceScanner
{
    public AIToHuman.Domain.Orders.EvidenceScanStatus Status { get; set; } = status;

    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public Task<AIToHuman.Domain.Orders.EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Failure is null
            ? Task.FromResult(Status)
            : Task.FromException<AIToHuman.Domain.Orders.EvidenceScanStatus>(Failure);
    }
}

/// <summary>配置提供者的替身：只认给定的键值，来源统一算作配置文件。</summary>
internal sealed class StubSettingsProvider(Dictionary<string, string> values) : ISettingsProvider
{
    public string? GetValue(string key) => values.TryGetValue(key, out var value) ? value : null;

    public SettingSource GetSource(string key) => values.ContainsKey(key) ? SettingSource.Configuration : SettingSource.Default;

    /// <summary>AI 相关配置的常用组合。</summary>
    public static StubSettingsProvider Ai(string apiKey = "test-key", string model = "test-model", int timeoutSeconds = 120, string baseUrl = "https://ark.invalid/api/v3") =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SettingKeys.AiBaseUrl] = baseUrl,
            [SettingKeys.AiApiKey] = apiKey,
            [SettingKeys.AiModel] = model,
            [SettingKeys.AiInactivityTimeoutSeconds] = timeoutSeconds.ToString(CultureInfo.InvariantCulture)
        });

    public static StubSettingsProvider Of(params (string Key, string Value)[] entries) =>
        new(entries.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}

internal static class Harness
{
    public static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    public static AiPlanningService CreateService(StubUpstreamHandler handler, int timeoutSeconds = 120, string apiKey = "test-key") =>
        new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            StubSettingsProvider.Ai(apiKey: apiKey, timeoutSeconds: timeoutSeconds),
            new FixedTimeProvider(Now),
            NullLogger<AiPlanningService>.Instance);

    public static IReadOnlyList<AiConversationMessage> Messages(params AiConversationMessage[] messages) => messages;

    public static AiConversationMessage User(string content) => new("user", content);

    public static AiConversationMessage Assistant(string content) => new("assistant", content);

    /// <summary>消费整个流；异常不抛出，便于同时断言“已产出的增量”和“最终错误”。</summary>
    public static async Task<(List<AiPlanningStreamEvent> Events, Exception? Error)> DrainAsync(AiPlanningService service, IReadOnlyList<AiConversationMessage> messages)
    {
        var events = new List<AiPlanningStreamEvent>();
        try
        {
            await foreach (var item in service.PlanStreamAsync(messages, CancellationToken.None))
            {
                events.Add(item);
            }

            return (events, null);
        }
        catch (Exception exception)
        {
            return (events, exception);
        }
    }

    public static string DeltaText(IEnumerable<AiPlanningStreamEvent> events) =>
        string.Concat(events.OfType<AiPlanningDeltaEvent>().Select(item => item.Text));

    public static AiConversationTurnResponse? Turn(IEnumerable<AiPlanningStreamEvent> events) =>
        events.OfType<AiPlanningCompletedEvent>().LastOrDefault()?.Turn;
}
