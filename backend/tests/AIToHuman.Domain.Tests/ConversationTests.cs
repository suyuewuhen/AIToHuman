using AIToHuman.Domain.Common;
using AIToHuman.Domain.Conversations;

namespace AIToHuman.Domain.Tests;

public sealed class ConversationTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Greeting_must_be_the_first_and_only_first_message()
    {
        var conversation = new Conversation(Guid.NewGuid(), _now);
        conversation.AppendAssistantGreeting("你好", _now);

        Assert.Single(conversation.Messages);
        Assert.Equal(ConversationRole.Assistant, conversation.Messages[0].Role);
        Assert.Throws<DomainException>(() => conversation.AppendAssistantGreeting("再来一次", _now));
    }

    [Fact]
    public void Conversation_requires_a_valid_owner()
    {
        Assert.Throws<DomainException>(() => new Conversation(Guid.Empty, _now));
    }

    [Fact]
    public void User_message_requires_the_greeting_first()
    {
        var conversation = new Conversation(Guid.NewGuid(), _now);

        Assert.Throws<DomainException>(() => conversation.AppendUserMessage("帮我取件", _now));
    }

    [Fact]
    public void Consecutive_user_messages_are_rejected()
    {
        var conversation = CreateConversation();
        conversation.AppendUserMessage("帮我取一份文件", _now);

        var error = Assert.Throws<DomainException>(() => conversation.AppendUserMessage("还有一个要求", _now));
        Assert.Equal("上一条用户消息还没有得到 AI 回复。", error.Message);
    }

    [Fact]
    public void Assistant_turn_must_follow_a_user_message()
    {
        var conversation = CreateConversation();

        var error = Assert.Throws<DomainException>(() => conversation.AppendAssistantTurn("请问在哪里取件？", false, null, _now));
        Assert.Equal("AI 回合必须紧跟在用户消息之后。", error.Message);
    }

    [Fact]
    public void Ready_to_draft_requires_plan_json()
    {
        var conversation = CreateConversation();
        conversation.AppendUserMessage("明天下午取件", _now);

        var error = Assert.Throws<DomainException>(() => conversation.AppendAssistantTurn("草稿已生成。", true, null, _now));
        Assert.Equal("标记需求已明确时必须同时保存草稿。", error.Message);
    }

    [Fact]
    public void Plan_json_is_dropped_when_draft_is_not_ready()
    {
        var conversation = CreateConversation();
        conversation.AppendUserMessage("明天下午取件", _now);

        var message = conversation.AppendAssistantTurn("还差一个地点。", false, """{"title":"不该保存"}""", _now);

        Assert.False(message.ReadyToDraft);
        Assert.Null(message.PlanJson);
    }

    [Fact]
    public void Message_content_is_validated_and_trimmed()
    {
        var conversation = CreateConversation();

        Assert.Throws<DomainException>(() => conversation.AppendUserMessage("   ", _now));
        Assert.Throws<DomainException>(() => conversation.AppendUserMessage(new string('你', ConversationLimits.MaxMessageLength + 1), _now));

        var message = conversation.AppendUserMessage("  帮我取件  ", _now);
        Assert.Equal("帮我取件", message.Content);
    }

    [Fact]
    public void History_returns_the_most_recent_messages()
    {
        var conversation = CreateConversation();
        for (var index = 0; index < 5; index++)
        {
            conversation.AppendUserMessage($"第 {index} 问", _now);
            conversation.AppendAssistantTurn($"第 {index} 答", false, null, _now);
        }

        var history = conversation.History(3);

        Assert.Equal(3, history.Count);
        Assert.Equal("第 3 答", history[0].Content);
        Assert.Equal("第 4 问", history[1].Content);
        Assert.Equal("第 4 答", history[^1].Content);
    }

    [Fact]
    public void History_window_must_be_positive()
    {
        var conversation = CreateConversation();

        Assert.Throws<DomainException>(() => conversation.History(0));
    }

    [Fact]
    public void Oldest_messages_are_trimmed_but_sequence_keeps_increasing()
    {
        var conversation = new Conversation(Guid.NewGuid(), _now);
        conversation.AppendAssistantGreeting("你好", _now);

        // 每轮追加两条，直到超过存储上限；序号必须保持单调，否则会与保留消息的序号冲突。
        // 注意：写入过程中条数会被截断回上限，因此这里用固定轮数而不是 while(Count <= 上限)。
        var rounds = ConversationLimits.MaxStoredMessages / 2 + 5;
        for (var index = 0; index < rounds; index++)
        {
            conversation.AppendUserMessage("问题", _now);
            conversation.AppendAssistantTurn("回答", false, null, _now);
        }

        Assert.Equal(ConversationLimits.MaxStoredMessages, conversation.Messages.Count);

        var sequences = conversation.Messages.Select(item => item.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(item => item).ToArray(), sequences);
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);
        Assert.True(sequences[0] > 1, "最早的回合应已被截断");
    }

    [Fact]
    public void Current_draft_returns_the_last_ready_turn()
    {
        var conversation = CreateConversation();
        Assert.Null(conversation.CurrentDraft());

        conversation.AppendUserMessage("明天下午取件", _now);
        conversation.AppendAssistantTurn("好的。", true, """{"title":"第一版"}""", _now);
        conversation.AppendUserMessage("改成后天", _now);
        conversation.AppendAssistantTurn("已更新。", true, """{"title":"第二版"}""", _now);

        Assert.Equal("""{"title":"第二版"}""", conversation.CurrentDraft()!.PlanJson);
    }

    [Fact]
    public void Rehydrate_restores_messages_and_sequence()
    {
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var messages = new[]
        {
            ConversationMessage.Rehydrate(Guid.NewGuid(), conversationId, ConversationRole.Assistant, "你好", 1, _now, false, null),
            ConversationMessage.Rehydrate(Guid.NewGuid(), conversationId, ConversationRole.User, "帮我取件", 2, _now, false, null)
        };

        var conversation = Conversation.Rehydrate(conversationId, userId, _now, _now.AddMinutes(1), messages);
        var appended = conversation.AppendAssistantTurn("请问在哪里取件？", false, null, _now.AddMinutes(2));

        Assert.Equal(3, appended.Sequence);
        Assert.Equal(3, conversation.Messages.Count);
    }

    [Fact]
    public void Local_offsets_are_normalized_to_utc()
    {
        var localNow = new DateTimeOffset(2026, 9, 10, 16, 0, 0, TimeSpan.FromHours(8));
        var conversation = new Conversation(Guid.NewGuid(), localNow);
        var message = conversation.AppendAssistantGreeting("你好", localNow);

        Assert.Equal(TimeSpan.Zero, conversation.CreatedAt.Offset);
        Assert.Equal(TimeSpan.Zero, message.CreatedAt.Offset);
        Assert.Equal(localNow.ToUniversalTime(), message.CreatedAt);
    }

    private Conversation CreateConversation()
    {
        var conversation = new Conversation(Guid.NewGuid(), _now);
        conversation.AppendAssistantGreeting("你好，先告诉我你希望有人帮你完成什么？", _now);
        return conversation;
    }
}
