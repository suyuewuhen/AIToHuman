using System.Net;
using System.Text.Json;
using AIToHuman.Contracts.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 用模拟火山引擎 SSE 的上游替身覆盖 AI 多轮协议：分片 JSON、转义字符、缺少 [DONE]、
/// 截断响应、超时、上游错误，以及对话校验规则。
/// </summary>
public sealed class AiPlanningServiceTests
{
    private const string IncompleteTurnJson = """{"assistantMessage":"你好，请告诉我在哪里取件？","readyToDraft":false,"plan":null}""";

    private const string EscapedTurnJson = """{"assistantMessage":"第一行\n第二行 \"引号\" \\反斜杠 \u4F60好","readyToDraft":false,"plan":null}""";

    private const string ReadyTurnJson = """{"assistantMessage":"信息已经齐全，请检查草稿。","readyToDraft":true,"plan":{"title":"代取文件","description":"到前台取一份普通文件并送到指定地点","district":"浦东新区","deadline":"2026-09-12T15:00:00+08:00","acceptanceCriteria":["上传取件码照片","交付时拍照确认"],"suggestedReward":60,"clarifications":[]}}""";

    [Fact]
    public async Task Streams_only_assistant_message_across_chunked_sse_events()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body(Sse.Split(IncompleteTurnJson, 7))));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("明天下午帮我取一份文件")));

        Assert.Null(error);
        var deltas = events.OfType<AiPlanningDeltaEvent>().ToArray();
        Assert.True(deltas.Length > 1, "分片响应应产生多个增量事件");
        Assert.Equal("你好，请告诉我在哪里取件？", Harness.DeltaText(events));
        foreach (var delta in deltas)
        {
            Assert.DoesNotContain("assistantMessage", delta.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("readyToDraft", delta.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("plan", delta.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("{", delta.Text, StringComparison.Ordinal);
        }

        var turn = Harness.Turn(events);
        Assert.NotNull(turn);
        Assert.False(turn!.ReadyToDraft);
        Assert.Null(turn.Plan);
    }

    [Fact]
    public async Task Decodes_json_escapes_in_streamed_and_final_text()
    {
        const string expected = "第一行\n第二行 \"引号\" \\反斜杠 你好";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body(Sse.Split(EscapedTurnJson, 5))));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.Equal(expected, Harness.DeltaText(events));
        Assert.Equal(expected, Harness.Turn(events)!.AssistantMessage);
    }

    [Fact]
    public async Task Completes_when_upstream_ends_without_done_marker()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson], terminateWithDone: false)));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.NotNull(Harness.Turn(events));
        Assert.Equal("你好，请告诉我在哪里取件？", Harness.DeltaText(events));
    }

    [Fact]
    public async Task Stops_reading_after_done_marker()
    {
        var body = Sse.Body([IncompleteTurnJson], terminateWithDone: false)
            + "data: [DONE]\n\n"
            + Sse.RawBody("""{"error":"[DONE] 之后的载荷不应被读取"}""");
        var handler = new StubUpstreamHandler(_ => Sse.Respond(body));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.NotNull(Harness.Turn(events));
    }

    [Fact]
    public async Task Retries_and_tells_the_page_to_restart_when_json_is_truncated()
    {
        const string truncated = "{\"assistantMessage\":\"已经收到了你的需求\"";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([truncated])));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        // JsonDocument.Parse 抛出的是 JsonException 的派生类型 JsonReaderException，这里统一包装为格式异常。
        var format = Assert.IsType<AiPlanningFormatException>(error);
        Assert.Contains("不是合法的任务草稿", format.Message);
        Assert.True(format.VisibleLength > 0, "截断前已经输出过文字，重试需要通知页面清空");
        Assert.Null(Harness.Turn(events));

        // 两次尝试都失败，所以只重试一次；重试前必须发出 restart，避免两段回复拼接。
        Assert.Equal(2, handler.RequestBodies.Count);
        var restartIndex = events.FindIndex(item => item is AiPlanningRestartEvent);
        Assert.True(restartIndex > 0, "重试前应发出 restart 事件");
        Assert.Equal("已经收到了你的需求", Harness.DeltaText(events.Skip(restartIndex + 1)));
        Assert.Empty(events.OfType<AiPlanningCompletedEvent>());
    }

    [Fact]
    public async Task Surfaces_upstream_error_when_body_is_empty()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(string.Empty));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<AiPlanningUpstreamException>(error);
        Assert.Contains("没有返回对话内容", exception.Message);
    }

    [Fact]
    public async Task Surfaces_upstream_http_error_with_status_and_message()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond("""{"error":{"message":"rate limited"}}""", HttpStatusCode.TooManyRequests));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<AiPlanningUpstreamException>(error);
        Assert.Contains("429", exception.Message);
        Assert.Contains("rate limited", exception.Message);
    }

    [Fact]
    public async Task Surfaces_upstream_http_error_without_json_body()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond("<html>bad gateway</html>", HttpStatusCode.BadGateway));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<AiPlanningUpstreamException>(error);
        Assert.Equal("AI 服务请求失败（502）。", exception.Message);
    }

    [Fact]
    public async Task Surfaces_error_object_inside_stream()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.RawBody("""{"error":"insufficient quota"}""")));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<AiPlanningUpstreamException>(error);
        Assert.Equal("insufficient quota", exception.Message);
    }

    [Fact]
    public async Task Times_out_when_upstream_stalls()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Stall());
        var service = Harness.CreateService(handler, timeoutSeconds: 5);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<AiPlanningTimeoutException>(error);
        Assert.Contains("5 秒没有返回数据", exception.Message);
        Assert.Single(handler.RequestBodies);   // 超时不重试，直接交给页面
    }

    [Fact]
    public async Task Retries_without_restart_when_nothing_was_streamed_yet()
    {
        // 第一次返回完全不是 JSON，页面什么都没看到，因此不需要 restart。
        var attempt = 0;
        var handler = new StubUpstreamHandler(_ =>
        {
            attempt++;
            return attempt == 1 ? Sse.Respond(Sse.Body(["这不是 JSON"])) : Sse.Respond(Sse.Body([IncompleteTurnJson]));
        });
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.Empty(events.OfType<AiPlanningRestartEvent>());
        Assert.Equal("你好，请告诉我在哪里取件？", Harness.DeltaText(events));
        Assert.NotNull(Harness.Turn(events));
        Assert.Equal(2, handler.RequestBodies.Count);

        // 第二次请求要把失败原因作为修复指令带上。
        using var document = JsonDocument.Parse(handler.RequestBodies[1]);
        var messages = document.RootElement.GetProperty("messages");
        var repair = messages[messages.GetArrayLength() - 1].GetProperty("content").GetString();
        Assert.Contains("没有通过校验", repair);
    }

    [Fact]
    public async Task Retries_a_plan_that_fails_schema_validation()
    {
        const string withoutTitle = """{"assistantMessage":"草稿已生成。","readyToDraft":true,"plan":{"description":"说明","district":"浦东新区","deadline":"2026-09-12T15:00:00+08:00","acceptanceCriteria":["完成"],"suggestedReward":50,"clarifications":[]}}""";
        var attempt = 0;
        var handler = new StubUpstreamHandler(_ =>
        {
            attempt++;
            return attempt == 1 ? Sse.Respond(Sse.Body([withoutTitle])) : Sse.Respond(Sse.Body([ReadyTurnJson]));
        });
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("明天下午取件")));

        Assert.Null(error);
        Assert.Single(events.OfType<AiPlanningRestartEvent>());
        var turn = Harness.Turn(events);
        Assert.True(turn!.ReadyToDraft);
        Assert.Equal("代取文件", turn.Plan!.Title);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Gives_up_after_one_retry_with_a_friendly_message()
    {
        const string withoutTitle = """{"assistantMessage":"草稿已生成。","readyToDraft":true,"plan":{"description":"说明","district":"浦东新区","deadline":"2026-09-12T15:00:00+08:00","acceptanceCriteria":["完成"],"suggestedReward":50,"clarifications":[]}}""";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([withoutTitle])));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("明天下午取件")));

        var exception = Assert.IsType<AiPlanningFormatException>(error);
        Assert.Contains("缺少任务标题", exception.Message);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Single(events.OfType<AiPlanningRestartEvent>());
        Assert.Null(Harness.Turn(events));
    }

    [Fact]
    public async Task Parses_plan_only_when_ready_to_draft()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([ReadyTurnJson])));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("明天下午取件，送到我公司")));

        Assert.Null(error);
        var turn = Harness.Turn(events);
        Assert.NotNull(turn);
        Assert.True(turn!.ReadyToDraft);
        Assert.NotNull(turn.Plan);
        Assert.Equal("代取文件", turn.Plan!.Title);
        Assert.Equal("浦东新区", turn.Plan.District);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 15, 0, 0, TimeSpan.FromHours(8)), turn.Plan.Deadline);
        Assert.Equal(60m, turn.Plan.SuggestedReward);
        Assert.Equal(2, turn.Plan.AcceptanceCriteria.Count);
        Assert.Equal("volcengine", turn.Plan.Provider);
        Assert.Equal("信息已经齐全，请检查草稿。", Harness.DeltaText(events));
    }

    [Fact]
    public async Task Keeps_plan_null_when_not_ready_to_draft()
    {
        const string planWithoutReady = """{"assistantMessage":"还差一个地点。","readyToDraft":false,"plan":{"title":"代取文件","description":"说明","district":"浦东新区","deadline":"2026-09-12T15:00:00+08:00","acceptanceCriteria":["完成"],"suggestedReward":50,"clarifications":[]}}""";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([planWithoutReady])));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        var turn = Harness.Turn(events);
        Assert.False(turn!.ReadyToDraft);
        Assert.Null(turn.Plan);
    }

    [Fact]
    public async Task Fails_when_ready_to_draft_without_plan()
    {
        const string readyWithoutPlan = """{"assistantMessage":"可以生成了。","readyToDraft":true,"plan":null}""";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([readyWithoutPlan])));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<AiPlanningFormatException>(error);
        Assert.Contains("没有返回任务草稿", exception.Message);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Theory]
    [InlineData("2020-01-01T00:00:00+08:00")]
    [InlineData("不是时间")]
    [InlineData("")]
    public async Task Falls_back_to_one_day_ahead_when_deadline_is_unusable(string deadline)
    {
        var json = "{\"assistantMessage\":\"请检查草稿。\",\"readyToDraft\":true,\"plan\":{\"title\":\"代取文件\",\"description\":\"说明\",\"district\":\"浦东新区\",\"deadline\":\"" + deadline + "\",\"acceptanceCriteria\":[\"完成\"],\"suggestedReward\":50,\"clarifications\":[]}}";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([json])));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.Equal(Harness.Now.AddDays(1), Harness.Turn(events)!.Plan!.Deadline);
    }

    [Fact]
    public async Task Reads_deadline_without_offset_as_beijing_time()
    {
        const string json = """{"assistantMessage":"请检查草稿。","readyToDraft":true,"plan":{"title":"代取文件","description":"说明","district":"浦东新区","deadline":"2026-09-12T15:00:00","acceptanceCriteria":["完成"],"suggestedReward":50,"clarifications":[]}}""";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([json])));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 15, 0, 0, TimeSpan.FromHours(8)), Harness.Turn(events)!.Plan!.Deadline);
    }

    [Fact]
    public async Task Strips_markdown_code_fence()
    {
        var fenced = "```json\n" + IncompleteTurnJson + "\n```";
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body(Sse.Split(fenced, 11))));
        var service = Harness.CreateService(handler);

        var (events, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        Assert.Null(error);
        Assert.Equal("你好，请告诉我在哪里取件？", Harness.Turn(events)!.AssistantMessage);
    }

    [Fact]
    public async Task Sends_bearer_token_stream_flag_and_model_upstream()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler);

        await Harness.DrainAsync(service, Harness.Messages(Harness.User("你好")));

        Assert.Equal("Bearer test-key", handler.AuthorizationHeader);
        Assert.Contains("text/event-stream", handler.AcceptHeaders);
        Assert.Equal("https://ark.invalid/api/v3/chat/completions", handler.RequestUri);

        var body = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("test-model", document.RootElement.GetProperty("model").GetString());
        var messages = document.RootElement.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("北京时间", messages[0].GetProperty("content").GetString());
        var lastMessage = messages[messages.GetArrayLength() - 1];
        Assert.Equal("user", lastMessage.GetProperty("role").GetString());
        Assert.Equal("你好", lastMessage.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Requires_configured_api_key()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler, apiKey: string.Empty);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User("帮我取件")));

        var exception = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("尚未配置 API Key", exception.Message);
    }

    [Fact]
    public async Task Rejects_conversation_that_does_not_end_with_user_turn()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.Assistant("你好，请问要做什么？")));

        var exception = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("最后一条对话必须来自用户。", exception.Message);
    }

    [Fact]
    public async Task Rejects_conversation_longer_than_thirty_messages()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler);
        var messages = Enumerable.Range(0, 30)
            .Select(index => Harness.Assistant($"第 {index} 轮"))
            .Append(Harness.User("最后一句"))
            .ToArray();

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(messages));

        var exception = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("本次对话过长，请生成草稿或重新开始。", exception.Message);
    }

    [Fact]
    public async Task Rejects_message_longer_than_four_thousand_characters()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(Harness.User(new string('你', 4001))));

        var exception = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("每条对话内容应为 1 至 4000 个字符。", exception.Message);
    }

    [Fact]
    public async Task Rejects_unknown_role()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages(new AiConversationMessage("system", "越权角色"), Harness.User("帮我取件")));

        var exception = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("对话角色不正确。", exception.Message);
    }

    [Fact]
    public async Task Rejects_empty_conversation()
    {
        var handler = new StubUpstreamHandler(_ => Sse.Respond(Sse.Body([IncompleteTurnJson])));
        var service = Harness.CreateService(handler);

        var (_, error) = await Harness.DrainAsync(service, Harness.Messages());

        var exception = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("请先告诉 AI 你想完成什么。", exception.Message);
    }
}
