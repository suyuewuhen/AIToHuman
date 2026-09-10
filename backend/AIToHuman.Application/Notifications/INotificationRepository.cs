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

    void Add(Notification notification);
    void Save(Notification notification);

    /// <summary>整批标记已读，返回受影响行数。只更新未读记录，已读不会被覆盖。</summary>
    int MarkAllRead(Guid userId, DateTimeOffset now);
}
