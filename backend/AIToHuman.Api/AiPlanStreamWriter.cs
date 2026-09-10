using System.Text.Json;
using AIToHuman.Contracts.Tasks;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Api;

/// <summary>
/// 负责把 AI 规划流写成 Server-Sent Events 帧。
/// 从最小 API 端点中抽出来，便于在不启动主机的情况下验证线格式（见 AIToHuman.IntegrationTests）。
/// </summary>
public static class AiPlanStreamWriter
{
    public const string DeltaEventName = "delta";
    public const string CompleteEventName = "complete";
    public const string ErrorEventName = "error";
    public const string RestartEventName = "restart";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>在开始写流之前设置响应头；一旦开始写 SSE，就无法再用 HTTP 状态码表达失败。</summary>
    public static void PrepareResponse(HttpResponse response)
    {
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache, no-transform";
        response.Headers.Append("X-Accel-Buffering", "no");
    }

    /// <summary>只包含可展示给用户的自然语言增量。</summary>
    public static Task WriteDeltaAsync(HttpResponse response, string text, CancellationToken cancellationToken) =>
        WriteEventAsync(response, DeltaEventName, new { text }, cancellationToken);

    /// <summary>本轮状态；只有信息完整时 <c>turn.plan</c> 才非空。</summary>
    public static Task WriteCompleteAsync(HttpResponse response, AiConversationTurnResponse turn, CancellationToken cancellationToken) =>
        WriteEventAsync(response, CompleteEventName, new { turn }, cancellationToken);

    /// <summary>重试前清空页面上的半截回复。</summary>
    public static Task WriteRestartAsync(HttpResponse response, string reason, CancellationToken cancellationToken) =>
        WriteEventAsync(response, RestartEventName, new { reason }, cancellationToken);

    /// <summary>流内错误；错误细节必须是可展示文案，不能泄露内部 JSON。</summary>
    public static Task WriteErrorAsync(HttpResponse response, Exception exception, CancellationToken cancellationToken) =>
        WriteEventAsync(response, ErrorEventName, new { detail = DescribeError(exception) }, cancellationToken);

    public static string DescribeError(Exception exception) => exception switch
    {
        AiPlanningTimeoutException or AiPlanningUnavailableException or AiPlanningUpstreamException or AiPlanningFormatException or InvalidOperationException => exception.Message,
        DbUpdateException => "本轮对话保存失败，请重试。",
        JsonException => "AI 返回的任务草稿格式不正确，请重试。",
        _ => "AI 规划请求失败，请稍后重试。"
    };

    private static async Task WriteEventAsync(HttpResponse response, string eventName, object data, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(data, SerializerOptions);
        await response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}
