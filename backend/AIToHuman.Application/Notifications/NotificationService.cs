using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Orders;
using AIToHuman.Domain.Notifications;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Notifications;

/// <summary>
/// 通知用例：在业务事务内写入通知（充当 Outbox），查询收件箱与未读数，以及供后台派发任务使用的方法。
/// 同一个业务事件用确定性的 <see cref="Notification.EventId"/> 去重，重复调用不会产生第二条。
/// </summary>
public sealed class NotificationService(INotificationRepository repository, TimeProvider timeProvider)
{
    /// <summary>事件信封版本；客户端应忽略无法识别的更高版本。</summary>
    public const int EnvelopeVersion = 1;

    public const int MaxListLimit = 100;

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    /// <summary>订单被选中：通知服务者。事件键直接用订单 ID。</summary>
    public void EnqueueOrderCreated(Order order, DateTimeOffset now) =>
        EnqueueOrderEvent(order.WorkerId, NotificationTypes.OrderCreated, order.Id, order, now);

    /// <summary>订单状态变化：通知对方参与者。事件键由订单与状态推导，重放同一状态不会重复通知。</summary>
    public void EnqueueOrderStatusChanged(Order order, Guid recipientId, DateTimeOffset now) =>
        EnqueueOrderEvent(recipientId, NotificationTypes.OrderStatusChanged, DeriveEventId(order.Id, order.Status.ToString()), order, now);

    /// <summary>订单会话新消息：通知对方参与者。事件键用消息 ID，每条消息都是独立事件。</summary>
    public void EnqueueOrderMessage(Order order, OrderMessage message, Guid recipientId, DateTimeOffset now)
    {
        if (recipientId == Guid.Empty) return;
        if (repository.ExistsByEventId(message.Id)) return;

        var preview = message.Content.Length <= 60 ? message.Content : message.Content[..60];
        var payload = JsonSerializer.Serialize(new OrderMessageNotificationPayload(order.Id, order.Title, preview), PayloadOptions);
        repository.Add(new Notification(recipientId, message.Id, NotificationTypes.OrderMessageCreated, EnvelopeVersion, payload, now));
    }

    /// <summary>由订单 ID 与名称推导稳定的幂等键：同样的输入永远得到同一个键。</summary>
    private static Guid DeriveEventId(Guid orderId, string name)
    {
        Span<byte> buffer = stackalloc byte[16];
        orderId.TryWriteBytes(buffer);
        var hash = SHA256.HashData([.. buffer.ToArray(), .. Encoding.UTF8.GetBytes(name)]);
        return new Guid(hash.AsSpan(0, 16));
    }

    public NotificationListResponse List(Guid userId, int limit)
    {
        var effectiveLimit = limit is < 1 or > MaxListLimit ? 20 : limit;
        var items = repository.ListByUser(userId, effectiveLimit).Select(Map).ToArray();
        return new(items, repository.CountUnread(userId));
    }

    public int UnreadCount(Guid userId) => repository.CountUnread(userId);

    /// <summary>标记已读。<paramref name="ids"/> 为空表示全部已读；不属于该用户的 ID 会被忽略而不是报错。</summary>
    public int MarkRead(Guid userId, IReadOnlyList<Guid>? ids)
    {
        var now = timeProvider.GetUtcNow();
        if (ids is null || ids.Count == 0)
        {
            repository.MarkAllRead(userId, now);
            return repository.CountUnread(userId);
        }

        foreach (var id in ids.Distinct())
        {
            if (repository.Get(id) is not { } notification || notification.UserId != userId) continue;
            notification.MarkRead(now);
            repository.Save(notification);
        }

        return repository.CountUnread(userId);
    }

    public IReadOnlyCollection<Notification> ListPendingDispatch(int limit) => repository.ListPendingDispatch(limit is < 1 or > MaxListLimit ? 50 : limit);

    public NotificationEnvelope ToEnvelope(Notification notification) =>
        new(notification.EventId, notification.Type, notification.Version, notification.CreatedAt, ParsePayload(notification.PayloadJson));

    /// <summary>收到别的实例广播的扇出消息时，用同一套规则还原信封，保证两条路径推给客户端的内容一致。</summary>
    public static NotificationEnvelope ToEnvelope(NotificationFanoutMessage message) =>
        new(message.EventId, message.Type, message.Version, message.CreatedAt, ParsePayload(message.PayloadJson));

    /// <summary>
    /// 认领一条待派发通知。多实例部署时只有拿到 true 的那个实例负责推送，
    /// 因此同一周期里其它实例扫到同一条记录也不会重复推送。
    /// </summary>
    public bool TryClaimDispatch(Guid notificationId) => repository.TryClaim(notificationId, timeProvider.GetUtcNow());

    /// <summary>推送失败时撤回认领，让下个周期重试；绝不把没推出去的通知标记成已推送。</summary>
    public void ReleaseDispatch(Guid notificationId) => repository.ReleaseDispatch(notificationId);

    private void EnqueueOrderEvent(Guid recipientId, string type, Guid eventId, Order order, DateTimeOffset now)
    {
        if (recipientId == Guid.Empty) return;
        if (repository.ExistsByEventId(eventId)) return;

        var payload = JsonSerializer.Serialize(new OrderNotificationPayload(order.Id, order.Status.ToString(), order.Title), PayloadOptions);
        repository.Add(new Notification(recipientId, eventId, type, EnvelopeVersion, payload, now));
    }

    private static NotificationResponse Map(Notification notification) =>
        new(notification.Id, notification.EventId, notification.Type, notification.Version, notification.CreatedAt, notification.ReadAt, ParsePayload(notification.PayloadJson));

    /// <summary>载荷损坏时返回空对象，避免一条坏记录让整个收件箱或后台派发失败。</summary>
    private static JsonElement ParsePayload(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }
}
