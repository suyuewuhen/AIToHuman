using System.Text.Json;

namespace AIToHuman.Contracts.Notifications;

/// <summary>通知事件类型。客户端按类型决定提示文案，并始终通过 REST 拉取事实状态。</summary>
public static class NotificationTypes
{
    public const string OrderCreated = "order.created";
    public const string OrderStatusChanged = "order.statusChanged";
    public const string OrderMessageCreated = "order.messageCreated";
}

/// <summary>订单相关通知的载荷；只带定位信息，不带敏感内容。</summary>
public sealed record OrderNotificationPayload(Guid OrderId, string Status, string Title);

/// <summary>持久化通知，供收件箱列表与未读数使用。</summary>
public sealed record NotificationResponse(Guid Id, Guid EventId, string Type, int Version, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, JsonElement Payload);

/// <summary>SignalR 推送的信封：带事件 ID 与版本，客户端据此去重并判断能否解析。</summary>
public sealed record NotificationEnvelope(Guid EventId, string Type, int Version, DateTimeOffset OccurredAt, JsonElement Payload);

public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Items, int UnreadCount);

/// <summary>标记已读；<see cref="Ids"/> 为空表示全部标记为已读。</summary>
public sealed record MarkNotificationsReadRequest(Guid? UserId, IReadOnlyList<Guid>? Ids);
