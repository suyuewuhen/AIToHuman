using AIToHuman.Application.Tasks;

namespace AIToHuman.Api.Tasks;

/// <summary>
/// 过期任务的兜底扫描：已发布但超过截止时间仍无人被选中的任务会被置为过期、作废其报名并通知相关人。
/// 用户侧也可以在发布时就把截止时间设得合理，这里只是不让“没人管的过期任务”一直挂在大厅里。
/// 扫描幂等：任务一旦过期就不再出现在候选集里；两个实例同时扫到同一条时，只有写入成功的那一个算数。
/// </summary>
public sealed class TaskExpiryService(IServiceScopeFactory scopeFactory, ILogger<TaskExpiryService> logger) : BackgroundService
{
    /// <summary>扫描周期。过期不是秒级敏感，一分钟一轮足够。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>单轮处理上限，避免一次启动就长时间占用数据库连接。</summary>
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var result = scope.ServiceProvider.GetRequiredService<TaskService>().ExpireOverdueTasks(BatchSize);
                if (result.Expired > 0) logger.LogInformation("已把 {Count} 个超过截止时间的任务置为过期。", result.Expired);
                if (result.Skipped > 0) logger.LogDebug("{Count} 个过期候选在扫描期间被其它操作改动，留给下一轮。", result.Skipped);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 一轮失败不影响服务本身：等下一轮再来。
                logger.LogError(exception, "任务过期扫描失败，等下一轮再试。");
            }
        }
    }
}
