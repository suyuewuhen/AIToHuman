using AIToHuman.Application.Notifications;
using Microsoft.AspNetCore.SignalR;

namespace AIToHuman.Api.Notifications;

/// <summary>
/// Outbox 派发任务：周期性扫描未派发的通知并推送给在线客户端，成功后标记派发时间。
/// 推送失败不标记，下个周期会重试；通知本身已经落库，客户端重连后可以通过 REST 拉取。
/// 依赖注入的 <see cref="IHubContext{THub}"/> 是单例，仓储则按作用域解析。
/// </summary>
public sealed class NotificationDispatcher(
    IServiceScopeFactory scopeFactory,
    IHubContext<NotificationsHub> hub,
    ILogger<NotificationDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

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
            var envelope = notifications.ToEnvelope(notification);
            await hub.Clients
                .Group($"user:{notification.UserId:N}")
                .SendAsync("notification.created", envelope, cancellationToken);
            notifications.MarkDispatched(notification.Id);
        }
    }
}
