using System.Text.Json;
using AIToHuman.Contracts.Conversations;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Conversations;

namespace AIToHuman.Application.Conversations;

/// <summary>
/// 对话用例：创建会话、按服务端历史生成模型请求、把完成的回合落库，以及刷新恢复所需的读取。
/// 对话历史以服务端为准，前端不再上传完整历史。
/// </summary>
public sealed class ConversationService(IConversationRepository repository, TimeProvider timeProvider)
{
    public const string GreetingMessage = "你好，先告诉我你希望有人帮你完成什么？暂时不用想得很完整，我们可以一步一步确认。";

    private static readonly JsonSerializerOptions PlanJsonOptions = new(JsonSerializerDefaults.Web);

    public ConversationResponse Create(Guid userId)
    {
        var now = timeProvider.GetUtcNow();
        var conversation = new Conversation(userId, now);
        conversation.AppendAssistantGreeting(GreetingMessage, now);
        repository.Add(conversation);
        return Map(conversation);
    }

    public ConversationResponse Get(Guid conversationId, Guid userId) => Map(GetOwned(conversationId, userId));

    public IReadOnlyCollection<ConversationSummaryResponse> List(Guid userId, int limit)
    {
        var effectiveLimit = limit is < 1 or > 100 ? 20 : limit;
        return repository.ListByUser(userId, effectiveLimit).Select(MapSummary).ToArray();
    }

    /// <summary>构造送给模型的历史，不包含本轮用户消息；超出窗口时丢弃最早的回合。</summary>
    public IReadOnlyList<AiConversationMessage> BuildHistory(Guid conversationId, Guid userId, int maxMessages)
    {
        var conversation = GetOwned(conversationId, userId);
        return conversation.History(maxMessages)
            .Select(item => new AiConversationMessage(item.Role == ConversationRole.User ? "user" : "assistant", item.Content))
            .ToArray();
    }

    /// <summary>
    /// 一个回合完成后再写入用户消息与 AI 回复，因此模型失败时对话保持原样，前端可以重试同一句话。
    /// </summary>
    public ConversationResponse AppendTurn(Guid conversationId, Guid userId, string userMessage, AiConversationTurnResponse turn)
    {
        var conversation = GetOwned(conversationId, userId);
        var now = timeProvider.GetUtcNow();
        var readyToDraft = turn.ReadyToDraft && turn.Plan is not null;

        conversation.AppendTurn(userMessage, turn.AssistantMessage, readyToDraft, turn.Plan is null ? null : JsonSerializer.Serialize(turn.Plan, PlanJsonOptions), now);
        repository.Save(conversation);
        return Map(conversation);
    }

    private Conversation GetOwned(Guid conversationId, Guid userId)
    {
        var conversation = repository.Get(conversationId) ?? throw new KeyNotFoundException("对话不存在。");
        if (conversation.UserId != userId) throw new UnauthorizedAccessException("只有对话所有者可以访问该对话。");
        return conversation;
    }

    private static ConversationResponse Map(Conversation conversation) => new(
        conversation.Id,
        conversation.UserId,
        conversation.CreatedAt,
        conversation.UpdatedAt,
        conversation.Messages.Select(MapMessage).ToArray(),
        DeserializePlan(conversation.CurrentDraft()?.PlanJson));

    private static ConversationMessageResponse MapMessage(ConversationMessage message) => new(
        message.Id,
        message.Role == ConversationRole.User ? "user" : "assistant",
        message.Content,
        message.CreatedAt,
        message.ReadyToDraft,
        message.ReadyToDraft ? DeserializePlan(message.PlanJson) : null);

    private static ConversationSummaryResponse MapSummary(Conversation conversation)
    {
        var opening = conversation.Messages.FirstOrDefault(item => item.Role == ConversationRole.User)?.Content ?? "新对话";
        var preview = opening.Length <= 40 ? opening : opening[..40];
        return new(conversation.Id, conversation.CreatedAt, conversation.UpdatedAt, conversation.Messages.Count, preview, conversation.CurrentDraft() is not null);
    }

    private static AiTaskPlanResponse? DeserializePlan(string? planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<AiTaskPlanResponse>(planJson, PlanJsonOptions);
        }
        catch (JsonException)
        {
            // 历史草稿损坏时按“没有草稿”处理，不能因为一条旧记录让整个会话无法读取。
            return null;
        }
    }
}
