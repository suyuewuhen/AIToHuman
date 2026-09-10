using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Orders;

/// <summary>
/// 订单会话里的一条消息。只有订单双方可以读写，因此用单个 <see cref="ReadAt"/> 表示
/// “对方已读”：对查看者来说，未读 = 不是自己发的且 <see cref="ReadAt"/> 为空。
/// </summary>
public sealed class OrderMessage
{
    public const int MaxContentLength = 2000;

    private OrderMessage() { }

    public OrderMessage(Guid orderId, Guid senderId, string content, DateTimeOffset createdAt)
    {
        if (orderId == Guid.Empty) throw new DomainException("消息必须关联有效订单。");
        if (senderId == Guid.Empty) throw new DomainException("消息必须有发送者。");

        var trimmed = content?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxContentLength) throw new DomainException($"消息内容应为 1 至 {MaxContentLength} 个字符。");

        Id = Guid.NewGuid();
        OrderId = orderId;
        SenderId = senderId;
        Content = trimmed;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid SenderId { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static OrderMessage Rehydrate(Guid id, Guid orderId, Guid senderId, string content, DateTimeOffset createdAt, DateTimeOffset? readAt) =>
        new(orderId, senderId, content, createdAt)
        {
            Id = id,
            ReadAt = readAt is null ? null : UtcTimestamp.Normalize(readAt.Value)
        };

    /// <summary>对查看者是否未读。</summary>
    public bool IsUnreadFor(Guid viewerId) => SenderId != viewerId && !IsRead;

    /// <summary>标记对方已读；重复调用是空操作。</summary>
    public void MarkRead(DateTimeOffset now)
    {
        if (ReadAt is not null) return;
        ReadAt = UtcTimestamp.Normalize(now);
    }
}
