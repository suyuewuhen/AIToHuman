using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Notifications;

/// <summary>
/// 一条持久化通知，同时充当 Outbox 记录：写入发生在业务事务内，派发由后台任务完成。
/// <see cref="EventId"/> 是幂等键，同一个业务事件重复入队只会保留一条。
/// </summary>
public sealed class Notification
{
    private Notification() { }

    public Notification(Guid userId, Guid eventId, string type, int version, string payloadJson, DateTimeOffset createdAt)
    {
        if (userId == Guid.Empty) throw new DomainException("通知必须有接收者。");
        if (eventId == Guid.Empty) throw new DomainException("通知必须带事件标识。");
        if (string.IsNullOrWhiteSpace(type) || type.Trim().Length > 80) throw new DomainException("通知类型必须为 1 到 80 个字符。");
        if (version < 1) throw new DomainException("通知事件版本必须大于零。");
        if (string.IsNullOrWhiteSpace(payloadJson)) throw new DomainException("通知必须包含载荷。");
        if (payloadJson.Length > 8000) throw new DomainException("通知载荷不能超过 8000 个字符。");

        Id = Guid.NewGuid();
        UserId = userId;
        EventId = eventId;
        Type = type.Trim();
        Version = version;
        PayloadJson = payloadJson;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
    }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid EventId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>已推送给在线客户端的时间；为空表示还在 Outbox 里等待派发。</summary>
    public DateTimeOffset? DispatchedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public bool IsRead => ReadAt is not null;

    public static Notification Rehydrate(Guid id, Guid userId, Guid eventId, string type, int version, string payloadJson, DateTimeOffset createdAt, DateTimeOffset? dispatchedAt, DateTimeOffset? readAt) => new(userId, eventId, type, version, payloadJson, createdAt)
    {
        Id = id,
        DispatchedAt = dispatchedAt is null ? null : UtcTimestamp.Normalize(dispatchedAt.Value),
        ReadAt = readAt is null ? null : UtcTimestamp.Normalize(readAt.Value)
    };

    /// <summary>标记已派发。重复调用是空操作，避免后台重试把时间戳写坏。</summary>
    public void MarkDispatched(DateTimeOffset now)
    {
        if (DispatchedAt is not null) return;
        DispatchedAt = UtcTimestamp.Normalize(now);
    }

    /// <summary>
    /// 撤销派发标记：推送失败（例如扇出通道不可用）时回滚认领。
    /// 不这样做的话，认领成功但没推出去的通知会被永久当成已推送，客户端再也等不到它。
    /// </summary>
    public void ReleaseDispatch() => DispatchedAt = null;

    /// <summary>标记已读。重复调用是空操作。</summary>
    public void MarkRead(DateTimeOffset now)
    {
        if (ReadAt is not null) return;
        ReadAt = UtcTimestamp.Normalize(now);
    }
}
