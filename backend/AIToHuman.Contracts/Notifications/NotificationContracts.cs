using System.Text.Json;

namespace AIToHuman.Contracts.Notifications;

/// <summary>通知事件类型。客户端按类型决定提示文案，并始终通过 REST 拉取事实状态。</summary>
public static class NotificationTypes
{
    public const string OrderCreated = "order.created";
    public const string OrderStatusChanged = "order.statusChanged";
    public const string OrderMessageCreated = "order.messageCreated";

    /// <summary>订单被取消：通知对方参与者，取消原因走 REST 详情。</summary>
    public const string OrderCancelled = "order.cancelled";

    /// <summary>任务超过截止时间仍无人被选中而自动过期：通知所有者与被作废的报名者。</summary>
    public const string TaskExpired = "task.expired";

    /// <summary>任务被所有者撤销或运营下架：通知报名中的服务者。</summary>
    public const string TaskCancelled = "task.cancelled";

    /// <summary>服务者撤回了报名：通知任务所有者（报名人数变了）。</summary>
    public const string TaskApplicationWithdrawn = "task.applicationWithdrawn";

    /// <summary>订单进入争议：通知对方参与者，双方都等运营处置。</summary>
    public const string OrderDisputed = "order.disputed";

    /// <summary>争议已由运营处置：通知双方参与者，结果走 REST 详情。</summary>
    public const string OrderDisputeResolved = "order.disputeResolved";

    /// <summary>任务的风险复核有了结论（放行或驳回）：通知任务所有者，结论与依据走 REST 详情。</summary>
    public const string TaskRiskReviewed = "task.riskReviewed";
}

/// <summary>订单相关通知的载荷；只带定位信息，不带敏感内容。</summary>
public sealed record OrderNotificationPayload(Guid OrderId, string Status, string Title);

/// <summary>任务相关通知的载荷；只带定位信息，不含执行地址等参与者层内容。</summary>
public sealed record TaskNotificationPayload(Guid TaskId, string Status, string Title);

/// <summary>报名变动通知的载荷：任务所有者据此知道有人撤回了报名。</summary>
public sealed record TaskApplicationNotificationPayload(Guid TaskId, Guid ApplicationId, Guid WorkerId, string Title);

/// <summary>
/// 风险复核结论的载荷：只带结论与原因代码，复核依据（运营写的原文）走 REST 的任务详情，
/// 避免把运营的内部说明塞进推送。
/// </summary>
public sealed record TaskRiskReviewNotificationPayload(Guid TaskId, string Title, string ReviewStatus, string Verdict, string? RuleCode, bool CanPublish);

/// <summary>持久化通知，供收件箱列表与未读数使用。</summary>
public sealed record NotificationResponse(Guid Id, Guid EventId, string Type, int Version, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt, JsonElement Payload);

/// <summary>SignalR 推送的信封：带事件 ID 与版本，客户端据此去重并判断能否解析。</summary>
public sealed record NotificationEnvelope(Guid EventId, string Type, int Version, DateTimeOffset OccurredAt, JsonElement Payload);

public sealed record NotificationListResponse(IReadOnlyList<NotificationResponse> Items, int UnreadCount);

/// <summary>标记已读；<see cref="Ids"/> 为空表示全部标记为已读。</summary>
public sealed record MarkNotificationsReadRequest(Guid? UserId, IReadOnlyList<Guid>? Ids);
