using AIToHuman.Application.Conversations;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Conversations;
using AIToHuman.Infrastructure.Conversations;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 对话用例：创建会话、按服务端历史构造模型请求、回合落库与刷新恢复。
/// 使用内存仓储，因此不需要数据库，也不需要模型。
/// </summary>
public sealed class ConversationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_persists_the_greeting_so_refresh_has_something_to_show()
    {
        var (service, _) = CreateService();
        var userId = Guid.NewGuid();

        var conversation = service.Create(userId);

        Assert.Equal(userId, conversation.UserId);
        var message = Assert.Single(conversation.Messages);
        Assert.Equal("assistant", message.Role);
        Assert.Equal(ConversationService.GreetingMessage, message.Content);
        Assert.Null(conversation.Draft);

        // 重新读取（相当于刷新页面）应得到同样的内容。
        var reloaded = service.Get(conversation.Id, userId);
        Assert.Single(reloaded.Messages);
        Assert.Equal(ConversationService.GreetingMessage, reloaded.Messages[0].Content);
    }

    [Fact]
    public void Append_turn_stores_both_messages_in_order()
    {
        var (service, _) = CreateService();
        var userId = Guid.NewGuid();
        var conversation = service.Create(userId);

        var updated = service.AppendTurn(conversation.Id, userId, "明天下午帮我取一份文件", new AiConversationTurnResponse("可以，你希望在哪里取件？", false, null));

        Assert.Equal(3, updated.Messages.Count);
        Assert.Equal(new[] { "assistant", "user", "assistant" }, updated.Messages.Select(item => item.Role));
        Assert.Equal("明天下午帮我取一份文件", updated.Messages[1].Content);

        var reloaded = service.Get(conversation.Id, userId);
        Assert.Equal(3, reloaded.Messages.Count);
        Assert.Equal("可以，你希望在哪里取件？", reloaded.Messages[2].Content);
    }

    [Fact]
    public void Append_turn_round_trips_the_draft_for_refresh_restore()
    {
        var (service, _) = CreateService();
        var userId = Guid.NewGuid();
        var conversation = service.Create(userId);
        var plan = new AiTaskPlanResponse("代取文件", "到前台取一份文件", "浦东新区", new DateTimeOffset(2026, 9, 12, 7, 0, 0, TimeSpan.Zero), ["上传取件码照片"], 60, [], "volcengine");

        service.AppendTurn(conversation.Id, userId, "明天下午取件，送到我公司", new AiConversationTurnResponse("信息已经齐全，请检查草稿。", true, plan));

        var reloaded = service.Get(conversation.Id, userId);
        Assert.NotNull(reloaded.Draft);
        Assert.Equal(plan.Title, reloaded.Draft!.Title);
        Assert.Equal(plan.District, reloaded.Draft.District);
        Assert.Equal(plan.Deadline, reloaded.Draft.Deadline);
        Assert.Equal(plan.SuggestedReward, reloaded.Draft.SuggestedReward);
        Assert.Equal(plan.AcceptanceCriteria, reloaded.Draft.AcceptanceCriteria);
        Assert.True(reloaded.Messages[^1].ReadyToDraft);
        Assert.Equal(plan.Title, reloaded.Messages[^1].Plan!.Title);
    }

    [Fact]
    public void A_failed_turn_leaves_the_conversation_untouched()
    {
        var (service, repository) = CreateService();
        var userId = Guid.NewGuid();
        var conversation = service.Create(userId);
        var tooLong = new string('你', ConversationLimits.MaxMessageLength + 1);

        // 模型回复超长时整个回合都应被拒绝，不能只留下用户消息。
        Assert.Throws<DomainException>(() => service.AppendTurn(conversation.Id, userId, "帮我取件", new AiConversationTurnResponse(tooLong, false, null)));

        Assert.Single(repository.Get(conversation.Id)!.Messages);
        Assert.Single(service.Get(conversation.Id, userId).Messages);
    }

    [Fact]
    public void Build_history_excludes_the_new_message_and_keeps_the_model_window()
    {
        var (service, _) = CreateService();
        var userId = Guid.NewGuid();
        var conversation = service.Create(userId);
        for (var index = 0; index < ConversationLimits.MaxHistoryMessages; index++)
        {
            service.AppendTurn(conversation.Id, userId, $"第 {index} 问", new AiConversationTurnResponse($"第 {index} 答", false, null));
        }

        var history = service.BuildHistory(conversation.Id, userId, ConversationLimits.MaxHistoryMessages - 1);

        Assert.Equal(ConversationLimits.MaxHistoryMessages - 1, history.Count);
        Assert.All(history, item => Assert.DoesNotContain("第 0 问", item.Content));
        Assert.Equal("assistant", history[^1].Role);
        // 端点会把本轮用户消息接在后面，总数仍需在模型上限内。
        Assert.True(history.Count + 1 <= ConversationLimits.MaxHistoryMessages);
    }

    [Fact]
    public void Conversations_are_private_to_their_owner()
    {
        var (service, _) = CreateService();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var conversation = service.Create(owner);

        Assert.Throws<UnauthorizedAccessException>(() => service.Get(conversation.Id, other));
        Assert.Throws<UnauthorizedAccessException>(() => service.BuildHistory(conversation.Id, other, 10));
        Assert.Throws<UnauthorizedAccessException>(() => service.AppendTurn(conversation.Id, other, "帮我取件", new AiConversationTurnResponse("好的", false, null)));
    }

    [Fact]
    public void Unknown_conversation_is_reported_as_missing()
    {
        var (service, _) = CreateService();

        Assert.Throws<KeyNotFoundException>(() => service.Get(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void List_returns_own_conversations_with_preview_and_draft_flag()
    {
        var repository = new InMemoryConversationRepository();
        var clock = new MutableTimeProvider(Now);
        var service = new ConversationService(repository, clock);
        var userId = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var older = service.Create(userId);
        service.AppendTurn(older.Id, userId, "帮我取一份重要文件并送到公司前台", new AiConversationTurnResponse("好的。", true, new AiTaskPlanResponse("代取文件", "说明", "浦东新区", Now.AddDays(1), ["完成"], 50, [], "volcengine")));
        clock.Advance(TimeSpan.FromMinutes(5));
        var newer = service.Create(userId);
        service.Create(otherUser);

        var conversations = service.List(userId, 20).ToArray();

        Assert.Equal(2, conversations.Length);
        Assert.Equal(newer.Id, conversations[0].Id);
        var summary = conversations[1];
        Assert.Equal(older.Id, summary.Id);
        Assert.Equal(3, summary.MessageCount);
        Assert.True(summary.HasDraft);
        Assert.Equal("帮我取一份重要文件并送到公司前台", summary.Preview);
    }

    [Fact]
    public void List_preview_is_truncated_for_long_openers()
    {
        var (service, _) = CreateService();
        var userId = Guid.NewGuid();
        var conversation = service.Create(userId);
        var longMessage = new string('需', 60);
        service.AppendTurn(conversation.Id, userId, longMessage, new AiConversationTurnResponse("好的。", false, null));

        var summary = Assert.Single(service.List(userId, 20));

        Assert.Equal(40, summary.Preview.Length);
    }

    private static (ConversationService Service, InMemoryConversationRepository Repository) CreateService()
    {
        var repository = new InMemoryConversationRepository();
        return (new ConversationService(repository, new FixedTimeProvider(Now)), repository);
    }
}
