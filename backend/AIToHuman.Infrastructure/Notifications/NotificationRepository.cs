using AIToHuman.Application.Notifications;
using AIToHuman.Domain.Notifications;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Notifications;

public sealed class EfNotificationRepository(TaskDbContext db) : INotificationRepository
{
    // 保持跟踪：标记已读/已派发需要写回同一行。
    public Notification? Get(Guid id) => db.Notifications.SingleOrDefault(item => item.Id == id) is { } record ? Map(record) : null;

    public bool ExistsByEventId(Guid eventId) => db.Notifications.AsNoTracking().Any(item => item.EventId == eventId);

    public IReadOnlyCollection<Notification> ListByUser(Guid userId, int limit) => db.Notifications
        .AsNoTracking()
        .Where(item => item.UserId == userId)
        .OrderByDescending(item => item.CreatedAt)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public int CountUnread(Guid userId) => db.Notifications.AsNoTracking().Count(item => item.UserId == userId && item.ReadAt == null);

    public IReadOnlyCollection<Notification> ListPendingDispatch(int limit) => db.Notifications
        .AsNoTracking()
        .Where(item => item.DispatchedAt == null)
        .OrderBy(item => item.CreatedAt)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    /// <summary>
    /// 条件更新即认领：只有把 DispatchedAt 从 NULL 改成时间的那个实例影响 1 行，其它实例影响 0 行。
    /// 用 ExecuteUpdate 而不是查出来再 SaveChanges，避免「读—改—写」之间被别的实例插进来。
    /// </summary>
    public bool TryClaim(Guid id, DateTimeOffset now) => db.Notifications
        .Where(item => item.Id == id && item.DispatchedAt == null)
        .ExecuteUpdate(setters => setters.SetProperty(item => item.DispatchedAt, now)) == 1;

    /// <summary>撤回认领：把已认领但仍未真正推出的记录放回 Outbox。</summary>
    public void ReleaseDispatch(Guid id) => db.Notifications
        .Where(item => item.Id == id && item.DispatchedAt != null)
        .ExecuteUpdate(setters => setters.SetProperty(item => item.DispatchedAt, (DateTimeOffset?)null));

    public void Add(Notification notification)
    {
        db.Notifications.Add(ToRecord(notification));
        db.SaveChanges();
    }

    public void Save(Notification notification)
    {
        var record = db.Notifications.Single(item => item.Id == notification.Id);
        record.DispatchedAt = notification.DispatchedAt;
        record.ReadAt = notification.ReadAt;
        db.SaveChanges();
    }

    public int MarkAllRead(Guid userId, DateTimeOffset now) => db.Notifications
        .Where(item => item.UserId == userId && item.ReadAt == null)
        .ExecuteUpdate(setters => setters.SetProperty(item => item.ReadAt, now));

    private static NotificationRecord ToRecord(Notification notification) => new()
    {
        Id = notification.Id,
        UserId = notification.UserId,
        EventId = notification.EventId,
        Type = notification.Type,
        Version = notification.Version,
        PayloadJson = notification.PayloadJson,
        CreatedAt = notification.CreatedAt,
        DispatchedAt = notification.DispatchedAt,
        ReadAt = notification.ReadAt
    };

    private static Notification Map(NotificationRecord record) => Notification.Rehydrate(
        record.Id, record.UserId, record.EventId, record.Type, record.Version, record.PayloadJson, record.CreatedAt, record.DispatchedAt, record.ReadAt);
}

public sealed class InMemoryNotificationRepository : INotificationRepository
{
    private readonly List<Notification> notifications = [];
    private readonly Lock gate = new();

    public Notification? Get(Guid id) { lock (gate) { return notifications.SingleOrDefault(item => item.Id == id); } }

    public bool ExistsByEventId(Guid eventId) { lock (gate) { return notifications.Any(item => item.EventId == eventId); } }

    public IReadOnlyCollection<Notification> ListByUser(Guid userId, int limit)
    {
        lock (gate)
        {
            return notifications.Where(item => item.UserId == userId).OrderByDescending(item => item.CreatedAt).Take(limit).ToArray();
        }
    }

    public int CountUnread(Guid userId) { lock (gate) { return notifications.Count(item => item.UserId == userId && !item.IsRead); } }

    public IReadOnlyCollection<Notification> ListPendingDispatch(int limit)
    {
        lock (gate)
        {
            return notifications.Where(item => item.DispatchedAt is null).OrderBy(item => item.CreatedAt).Take(limit).ToArray();
        }
    }

    public bool TryClaim(Guid id, DateTimeOffset now)
    {
        lock (gate)
        {
            if (notifications.SingleOrDefault(item => item.Id == id) is not { } notification || notification.DispatchedAt is not null) return false;
            notification.MarkDispatched(now);
            return true;
        }
    }

    public void ReleaseDispatch(Guid id)
    {
        lock (gate) { notifications.SingleOrDefault(item => item.Id == id)?.ReleaseDispatch(); }
    }

    public void Add(Notification notification) { lock (gate) { notifications.Add(notification); } }

    public void Save(Notification notification) { /* 内存实现保存的是同一个对象引用，无需写回。 */ }

    public int MarkAllRead(Guid userId, DateTimeOffset now)
    {
        lock (gate)
        {
            var pending = notifications.Where(item => item.UserId == userId && !item.IsRead).ToArray();
            foreach (var notification in pending) notification.MarkRead(now);
            return pending.Length;
        }
    }
}
