using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIToHuman.Contracts.Tasks;
using Microsoft.Extensions.Options;

public sealed class VolcengineAiOptions
{
    public string BaseUrl { get; set; } = "https://ark.cn-beijing.volces.com/api/v3";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "glm-4-7-251222";
    public int TimeoutSeconds { get; set; } = 45;
}

public sealed class AiPlanningService(HttpClient httpClient, IOptions<VolcengineAiOptions> options)
{
    private readonly VolcengineAiOptions settings = options.Value;
    public async Task<AiTaskPlanResponse> PlanAsync(AiTaskPlanRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new InvalidOperationException("AI 服务尚未配置 API Key，请设置 VolcengineAI__ApiKey。");
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new InvalidOperationException("请先描述你想完成的事情。");
        var payloadObject = new { model = settings.Model, temperature = 0.2, max_tokens = 1200, messages = new[] { new { role = "system", content = SystemPrompt }, new { role = "user", content = request.Prompt.Trim() } } };
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(payloadObject));
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{settings.BaseUrl.TrimEnd('/')}/chat/completions") { Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json") };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AI 服务请求失败（{(int)response.StatusCode}）。");
        using var document = JsonDocument.Parse(responseText);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        return ParsePlan(content);
    }
    private static AiTaskPlanResponse ParsePlan(string content)
    {
        var json = content.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal)) { var firstLine = json.IndexOf('\n'); var lastFence = json.LastIndexOf("```", StringComparison.Ordinal); if (firstLine >= 0 && lastFence > firstLine) json = json[(firstLine + 1)..lastFence].Trim(); }
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        var criteria = root.TryGetProperty("acceptanceCriteria", out var criteriaElement) && criteriaElement.ValueKind == JsonValueKind.Array ? criteriaElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item)).Take(8).ToArray() : Array.Empty<string>();
        return new(root.GetProperty("title").GetString() ?? "待确认任务", root.GetProperty("description").GetString() ?? string.Empty, root.GetProperty("district").GetString() ?? "待确认地点", ParseDeadline(root.TryGetProperty("deadline", out var deadline) ? deadline.GetString() : null), criteria, root.TryGetProperty("suggestedReward", out var reward) && reward.TryGetDecimal(out var amount) ? amount : 0, root.TryGetProperty("clarifications", out var questions) && questions.ValueKind == JsonValueKind.Array ? questions.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(item => !string.IsNullOrWhiteSpace(item)).Take(6).ToArray() : Array.Empty<string>(), "volcengine");
    }
    private static DateTimeOffset ParseDeadline(string? value) => DateTimeOffset.TryParse(value, out var result) ? result : DateTimeOffset.UtcNow.AddDays(1);
    private const string SystemPrompt = """你是 AIToHuman 的任务规划助手。把用户的自然语言需求整理为 JSON，不要输出 Markdown、解释或代码围栏。字段必须是：title（不超过80字）、description（保留关键上下文）、district（地点或待确认地点）、deadline（ISO 8601 时间；不确定时给未来24小时并在 clarifications 说明）、acceptanceCriteria（2到6条可验证标准）、suggestedReward（人民币整数，无法判断时为0）、clarifications（最多6个必须向用户确认的问题）。不要编造用户没有提供的地址、联系人、金额或承诺；不确定的信息写“待确认”。""";
}
