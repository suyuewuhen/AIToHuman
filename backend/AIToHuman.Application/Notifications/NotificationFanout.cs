using AIToHuman.Domain.Notifications;

namespace AIToHuman.Application.Notifications;

/// <summary>
/// 一条已经被本实例认领的通知，需要广播给所有实例，由持有该用户连接的实例推给客户端。
/// 载荷保持原始 JSON 字符串，跨越进程时不重新序列化，避免字段大小写或数字格式在两岸漂移。
/// </summary>
public sealed record NotificationFanoutMessage(
    Guid UserId,
    Guid NotificationId,
    Guid EventId,
    string Type,
    int Version,
    DateTimeOffset CreatedAt,
    string PayloadJson)
{
    public static NotificationFanoutMessage From(Notification notification) => new(
        notification.UserId,
        notification.Id,
        notification.EventId,
        notification.Type,
        notification.Version,
        notification.CreatedAt,
        notification.PayloadJson);
}

/// <summary>
/// 多实例通知扇出。派发任务认领一条通知后，通过这里广播给所有实例。
/// <see cref="Enabled"/> 为 false（运营未开启、未配置 Redis、或依赖不可用）时，
/// 派发方退化为只推给本实例的在线客户端——单实例部署的行为与没有这个组件时完全一致。
/// </summary>
public interface INotificationFanout
{
    /// <summary>是否已启用并具备可用配置。实现必须是廉价的（读内存快照），因为它会在派发热路径上被反复调用。</summary>
    bool Enabled { get; }

    /// <summary>
    /// 把认领到的通知广播出去。调用方应先看 <see cref="Enabled"/>；这里再发现未启用时抛
    /// <see cref="InvalidOperationException"/>，用于兜住「开关在两次读取之间被关掉」的竞态——静默返回会让通知被认领却没人推送。
    /// 广播失败同样必须抛出，让调用方撤回认领。
    /// </summary>
    Task PublishAsync(NotificationFanoutMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 订阅扇出消息，直到 <paramref name="cancellationToken"/> 取消为止（取消时正常返回，不抛异常）。
    /// 未启用时立即返回。消息处理失败不应该中断订阅。
    /// </summary>
    Task SubscribeAsync(Func<NotificationFanoutMessage, CancellationToken, Task> handler, CancellationToken cancellationToken);
}
