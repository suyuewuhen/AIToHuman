using AIToHuman.Application.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace AIToHuman.Api.Notifications;

/// <summary>
/// 订阅通知扇出频道：任何实例认领的通知都会广播到所有实例，持有该用户连接的实例负责推给本机客户端。
/// 未启用扇出（未配置 Redis 或运营关闭开关）时这里什么都不做，派发任务会退化为单实例直接推送。
/// 订阅断开后按固定间隔重连：Redis 重启、网络抖动都不需要重启应用。
/// </summary>
public sealed class NotificationFanoutSubscriber(
    INotificationFanout fanout,
    IHubContext<NotificationsHub> hub,
    ILogger<NotificationFanoutSubscriber> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!fanout.Enabled)
        {
            logger.LogInformation(
                "通知扇出当前未启用（notifications.fanout.enabled=false 或未配置 ConnectionStrings__Redis），派发任务直接推送本实例客户端；开启后 {Delay} 秒内自动接入，不需要重启。",
                ReconnectDelay.TotalSeconds);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 每次循环都重新读开关：运营后台打开扇出后，这里会自动接上，而不是要等下一次重启。
                if (fanout.Enabled)
                {
                    await fanout.SubscribeAsync(PushAsync, stoppingToken);
                    if (stoppingToken.IsCancellationRequested) break;
                    logger.LogWarning("通知扇出订阅意外结束，{Delay} 秒后重连。", ReconnectDelay.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "通知扇出订阅失败，{Delay} 秒后重试。", ReconnectDelay.TotalSeconds);
            }

            try
            {
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private Task PushAsync(NotificationFanoutMessage message, CancellationToken cancellationToken) =>
        NotificationDispatcher.PushAsync(hub, message.UserId, NotificationService.ToEnvelope(message), cancellationToken);
}
