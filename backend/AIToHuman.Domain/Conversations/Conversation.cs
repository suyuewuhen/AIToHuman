using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Conversations;

/// <summary>对话历史长度策略。超出上限时从最早的回合开始丢弃，保证请求始终在模型与接口的限额内。</summary>
public static class ConversationLimits
{
    /// <summary>单条消息的最大长度，与 AI 请求校验保持一致。</summary>
    public const int MaxMessageLength = 4000;

    /// <summary>最多保留的消息条数；超出后丢弃最早的。</summary>
    public const int MaxStoredMessages = 200;

    /// <summary>送给模型的最大历史条数（含本轮用户消息）。</summary>
    public const int MaxHistoryMessages = 30;
}

public enum ConversationRole
{
    User,
    Assistant
}

public sealed class ConversationMessage
{
    private ConversationMessage() { }

    internal ConversationMessage(Guid conversationId, ConversationRole role, string content, int sequence, DateTimeOffset createdAt, bool readyToDraft = false, string? planJson = null)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        Role = role;
        Content = content;
        Sequence = sequence;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
        ReadyToDraft = readyToDraft;
        PlanJson = planJson;
    }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public ConversationRole Role { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public int Sequence { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>只有 assistant 回合可能标记需求已明确。</summary>
    public bool ReadyToDraft { get; private set; }

    /// <summary>模型返回的草稿 JSON，按不透明字符串保存，由应用层负责序列化。</summary>
    public string? PlanJson { get; private set; }

    public static ConversationMessage Rehydrate(Guid id, Guid conversationId, ConversationRole role, string content, int sequence, DateTimeOffset createdAt, bool readyToDraft, string? planJson) => new(conversationId, role, content, sequence, createdAt, readyToDraft, planJson)
    {
        Id = id
    };
}

/// <summary>
/// 一轮 AI 需求澄清的对话记录。对话是用户输入与 AI 回复的权威历史，
/// 也是刷新页面、跨设备继续和历史截断的依据；但对话本身不构成用户对发布任务的授权。
/// </summary>
public sealed class Conversation
{
    private readonly List<ConversationMessage> _messages = [];

    /// <summary>单调递增的回合序号；截断历史时不回退，否则会与保留消息的序号冲突。</summary>
    private int _nextSequence = 1;

    private Conversation() { }

    public Conversation(Guid userId, DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty) throw new DomainException("对话必须属于有效用户。");
        Id = Guid.NewGuid();
        UserId = userId;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<ConversationMessage> Messages => _messages.AsReadOnly();

    public static Conversation Rehydrate(Guid id, Guid userId, DateTimeOffset createdAt, DateTimeOffset updatedAt, IEnumerable<ConversationMessage> messages)
    {
        var conversation = new Conversation
        {
            Id = id,
            UserId = userId,
            CreatedAt = UtcTimestamp.Normalize(createdAt),
            UpdatedAt = UtcTimestamp.Normalize(updatedAt)
        };
        conversation._messages.AddRange(messages.OrderBy(item => item.Sequence));
        conversation._nextSequence = conversation._messages.Count == 0 ? 1 : conversation._messages[^1].Sequence + 1;
        return conversation;
    }

    /// <summary>创建对话时写入开场白，保证历史的第一条始终来自 AI。</summary>
    public ConversationMessage AppendAssistantGreeting(string content, DateTimeOffset now)
    {
        if (_messages.Count > 0) throw new DomainException("开场白只能写入空对话。");
        return Append(ConversationRole.Assistant, content, now, readyToDraft: false, planJson: null);
    }

    /// <summary>追加用户消息。要求上一条不是用户消息，避免出现连续两轮用户输入。</summary>
    public ConversationMessage AppendUserMessage(string content, DateTimeOffset now)
    {
        if (_messages.Count == 0) throw new DomainException("对话缺少 AI 开场白。");
        if (_messages[^1].Role == ConversationRole.User) throw new DomainException("上一条用户消息还没有得到 AI 回复。");
        return Append(ConversationRole.User, content, now, readyToDraft: false, planJson: null);
    }

    /// <summary>追加 AI 回合。必须紧跟在用户消息之后，只有信息完整时才允许携带草稿。</summary>
    public ConversationMessage AppendAssistantTurn(string content, bool readyToDraft, string? planJson, DateTimeOffset now)
    {
        if (_messages.Count == 0) throw new DomainException("对话缺少 AI 开场白。");
        if (_messages[^1].Role != ConversationRole.User) throw new DomainException("AI 回合必须紧跟在用户消息之后。");
        if (readyToDraft && string.IsNullOrWhiteSpace(planJson)) throw new DomainException("标记需求已明确时必须同时保存草稿。");
        return Append(ConversationRole.Assistant, content, now, readyToDraft, readyToDraft ? planJson : null);
    }

    /// <summary>
    /// 原子地追加一个回合：先校验两段内容再写入，避免模型回复超长时只留下一条用户消息。
    /// </summary>
    public void AppendTurn(string userContent, string assistantContent, bool readyToDraft, string? planJson, DateTimeOffset now)
    {
        var trimmedAssistant = assistantContent?.Trim() ?? string.Empty;
        if (trimmedAssistant.Length is < 1 or > ConversationLimits.MaxMessageLength)
        {
            throw new DomainException($"消息内容应为 1 至 {ConversationLimits.MaxMessageLength} 个字符。");
        }

        AppendUserMessage(userContent, now);
        AppendAssistantTurn(trimmedAssistant, readyToDraft, planJson, now);
    }

    /// <summary>送给模型的最近历史；超出上限时从最早的回合开始丢弃。</summary>
    public IReadOnlyList<ConversationMessage> History(int maxMessages)
    {
        if (maxMessages <= 0) throw new DomainException("历史窗口必须为正数。");
        return _messages.Count <= maxMessages ? _messages.AsReadOnly() : _messages.Skip(_messages.Count - maxMessages).ToArray();
    }

    /// <summary>当前可用的草稿：最后一条标记 readyToDraft 的 AI 回合。</summary>
    public ConversationMessage? CurrentDraft() =>
        _messages.LastOrDefault(item => item.Role == ConversationRole.Assistant && item.ReadyToDraft && !string.IsNullOrWhiteSpace(item.PlanJson));

    private ConversationMessage Append(ConversationRole role, string content, DateTimeOffset now, bool readyToDraft, string? planJson)
    {
        var trimmed = content?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > ConversationLimits.MaxMessageLength)
        {
            throw new DomainException($"消息内容应为 1 至 {ConversationLimits.MaxMessageLength} 个字符。");
        }

        var message = new ConversationMessage(Id, role, trimmed, _nextSequence++, now, readyToDraft, planJson);
        _messages.Add(message);
        UpdatedAt = UtcTimestamp.Normalize(now);
        TrimTo(ConversationLimits.MaxStoredMessages);
        return message;
    }

    private void TrimTo(int maxMessages)
    {
        if (_messages.Count <= maxMessages) return;
        _messages.RemoveRange(0, _messages.Count - maxMessages);
    }
}
