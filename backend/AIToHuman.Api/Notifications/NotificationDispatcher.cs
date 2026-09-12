using AIToHuman.Application.Notifications;
using AIToHuman.Contracts.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace AIToHuman.Api.Notifications;

/// <summary>
/// Outbox 派发任务：周期性扫描未派发的通知，认领后推送给在线客户端。
///
/// 顺序是「先原子认领、再推送」：多实例部署时同一周期只有一个实例能认领成功，因此不会重复推送；
/// 推送失败则撤回认领，下个周期重试——绝不出现「标记成已推送但客户端没收到」的静默丢失。
///
/// 启用扇出（<see cref="INotificationFanout.Enabled"/>）时推的是 Redis 频道，由各实例的
/// <see cref="NotificationFanoutSubscriber"/> 负责落到本机客户端；广播失败时兜底推本实例在线客户端，
/// 未启用时直接本地推送。通知本身已经落库，客户端重连后可以通过 REST 拉取，扇出只是实时性的加分项。
/// </summary>
public sealed class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationsHub> hub,
    INotificationFanout fanout,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

    /// <summary>推送给客户端的 SignalR 方法名；扇出订阅方与本地推送必须一致。</summary>
    public const string PushMethod = "notification.created";

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "通知派发失败，将在下一个周期重试。");
            }
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

    /// <summary>派发一轮待推送通知；单独公开便于测试直接驱动一个周期。</summary>
    public async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();

        foreach (var notification in notifications.ListPendingDispatch(BatchSize))
        {
            if (!notifications.TryClaimDispatch(notification.Id)) continue;

            try
            {
                if (fanout.Enabled)
                {
                    try
                    {
                        await fanout.PublishAsync(NotificationFanoutMessage.From(notification), cancellationToken);
                        continue;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        // 扇出不可用（Redis 抖动、还没起来）不该让本实例的用户什么都收不到：
                        // 先兜底推给连在这里的客户端，其它实例的客户端仍能靠 REST 补齐。
                        logger.LogWarning(exception, "通知扇出广播失败，改为只推本实例在线客户端。");
                    }
                }

                await PushAsync(notification.UserId, notifications.ToEnvelope(notification), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                notifications.ReleaseDispatch(notification.Id);
                throw;
            }
            catch (Exception exception)
            {
                notifications.ReleaseDispatch(notification.Id);
                logger.LogWarning(exception, "通知 {NotificationId} 推送失败，已退回 Outbox 等待重试。", notification.Id);
            }
        }
    }

    /// <summary>把信封推给连在本实例上的该用户连接；扇出订阅方也复用同一段逻辑。</summary>
    public static Task PushAsync(IHubContext<NotificationsHub> hub, Guid userId, NotificationEnvelope envelope, CancellationToken cancellationToken) =>
        hub.Clients
            .Group($"user:{userId:N}")
            .SendAsync(PushMethod, envelope, cancellationToken);

    private Task PushAsync(Guid userId, NotificationEnvelope envelope, CancellationToken cancellationToken) =>
        PushAsync(hub, userId, envelope, cancellationToken);
}
