using AIToHuman.Domain.Notifications;

namespace AIToHuman.Application.Notifications;

public interface INotificationRepository
{
    Notification? Get(Guid id);

    /// <summary>幂等键查重：同一个业务事件只入队一次。</summary>
    bool ExistsByEventId(Guid eventId);

    IReadOnlyCollection<Notification> ListByUser(Guid userId, int limit);
    int CountUnread(Guid userId);

    /// <summary>Outbox：还没推送给客户端的通知。</summary>
    IReadOnlyCollection<Notification> ListPendingDispatch(int limit);

    /// <summary>
    /// 原子认领派发权：把未派发的记录写成已派发，成功返回 true。
    /// 实现必须是单条 SQL 的条件更新（或等价加锁），保证多实例并发下只有一方拿到 true。
    /// </summary>
    bool TryClaim(Guid id, DateTimeOffset now);

    /// <summary>撤回认领（推送失败时补偿），让这条通知回到 Outbox 等下一轮重试。</summary>
    void ReleaseDispatch(Guid id);

    void Add(Notification notification);
    void Save(Notification notification);

    /// <summary>整批标记已读，返回受影响行数。只更新未读记录，已读不会被覆盖。</summary>
    int MarkAllRead(Guid userId, DateTimeOffset now);
}
