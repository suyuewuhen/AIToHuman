using System.Text.Json;
using AIToHuman.Api;
using AIToHuman.Contracts.Tasks;
using Microsoft.AspNetCore.Http;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 验证 SSE 线格式：事件名、data 载荷字段、以及错误事件不会泄露内部细节。
/// 这些断言覆盖端点在 Program.cs 中使用的同一个 AiPlanStreamWriter。
/// </summary>
public sealed class AiPlanStreamWriterTests
{
    [Fact]
    public void PrepareResponse_sets_sse_headers()
    {
        var context = new DefaultHttpContext();

        AiPlanStreamWriter.PrepareResponse(context.Response);

        Assert.Equal("text/event-stream; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-cache, no-transform", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no", context.Response.Headers["X-Accel-Buffering"].ToString());
    }

    [Fact]
    public async Task Delta_event_carries_only_text()
    {
        var frame = await WriteAsync(response => AiPlanStreamWriter.WriteDeltaAsync(response, "你好，", CancellationToken.None));

        Assert.StartsWith("event: delta\n", frame);
        Assert.EndsWith("\n\n", frame);
        using var document = JsonDocument.Parse(DataOf(frame));
        Assert.Equal("你好，", document.RootElement.GetProperty("text").GetString());
        Assert.Single(document.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Complete_event_wraps_turn_in_camel_case()
    {
        var turn = new AiConversationTurnResponse(
            "信息已经齐全。",
            true,
            new AiTaskPlanResponse("代取文件", "到前台取一份文件", "浦东新区", new DateTimeOffset(2026, 9, 12, 7, 0, 0, TimeSpan.Zero), ["上传照片"], 60, [], "volcengine"));

        var frame = await WriteAsync(response => AiPlanStreamWriter.WriteCompleteAsync(response, turn, CancellationToken.None));

        Assert.StartsWith("event: complete\n", frame);
        using var document = JsonDocument.Parse(DataOf(frame));
        var payload = document.RootElement.GetProperty("turn");
        Assert.Equal("信息已经齐全。", payload.GetProperty("assistantMessage").GetString());
        Assert.True(payload.GetProperty("readyToDraft").GetBoolean());
        var plan = payload.GetProperty("plan");
        Assert.Equal("代取文件", plan.GetProperty("title").GetString());
        Assert.Equal(60, plan.GetProperty("suggestedReward").GetDecimal());
    }

    [Fact]
    public async Task Complete_event_keeps_plan_null_when_draft_is_not_ready()
    {
        var turn = new AiConversationTurnResponse("还差一个地点。", false, null);

        var frame = await WriteAsync(response => AiPlanStreamWriter.WriteCompleteAsync(response, turn, CancellationToken.None));

        using var document = JsonDocument.Parse(DataOf(frame));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("turn").GetProperty("plan").ValueKind);
    }

    [Fact]
    public async Task Error_event_maps_exceptions_to_user_facing_text()
    {
        Assert.Equal("AI 规划服务连续 120 秒没有返回数据。", await ErrorDetailAsync(new AiPlanningTimeoutException(120, new TimeoutException("inner"))));
        Assert.Equal("无法连接 AI 规划服务。", await ErrorDetailAsync(new AiPlanningUnavailableException(new HttpRequestException("inner"))));
        Assert.Equal("AI 服务请求失败（429）。", await ErrorDetailAsync(new AiPlanningUpstreamException("AI 服务请求失败（429）。")));
        Assert.Equal("AI 返回的任务草稿格式不正确，请重试。", await ErrorDetailAsync(new JsonException("bad payload")));
        Assert.Equal("AI 规划请求失败，请稍后重试。", await ErrorDetailAsync(new InvalidCastException("boom")));
    }

    [Fact]
    public async Task Error_event_does_not_leak_exception_details()
    {
        var frame = await WriteAsync(response => AiPlanStreamWriter.WriteErrorAsync(response, new InvalidCastException("内部堆栈细节"), CancellationToken.None));

        Assert.StartsWith("event: error\n", frame);
        Assert.DoesNotContain("InvalidCastException", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("内部堆栈细节", frame, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(DataOf(frame));
        Assert.Single(document.RootElement.EnumerateObject());
    }

    private static async Task<string> WriteAsync(Func<HttpResponse, Task> write)
    {
        var context = new DefaultHttpContext();
        var stream = new MemoryStream();
        context.Response.Body = stream;

        await write(context.Response);

        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static string DataOf(string frame) =>
        frame.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];

    private static async Task<string> ErrorDetailAsync(Exception exception)
    {
        var frame = await WriteAsync(response => AiPlanStreamWriter.WriteErrorAsync(response, exception, CancellationToken.None));
        using var document = JsonDocument.Parse(DataOf(frame));
        return document.RootElement.GetProperty("detail").GetString()!;
    }
}
