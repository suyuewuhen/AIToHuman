using AIToHuman.Application.Idempotency;

namespace AIToHuman.Api.Idempotency;

/// <summary>
/// 定期清理过期幂等记录的后台任务。
/// 策略见 <see cref="IdempotencyCleanup"/>：已完成保留 24 小时、未完成占位保留 10 分钟，单次最多 500 条。
/// 清理失败只记警告并在下一个周期重试——它不该影响任何请求。
/// </summary>
public sealed class IdempotencyCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<IdempotencyCleanupService> logger) : BackgroundService
{
    /// <summary>扫描周期。策略本身以小时计，所以每小时扫一次就够。</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("幂等记录清理已启动：{Policy}", IdempotencyCleanup.Describe());

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);

                using var scope = scopeFactory.CreateScope();
                var result = scope.ServiceProvider.GetRequiredService<IdempotencyCleanup>().Sweep();
                if (result.Deleted > 0)
                {
                    logger.LogInformation("清理了 {Count} 条过期幂等记录。", result.Deleted);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "清理过期幂等记录失败，将在下一个周期重试。");
            }
        }
    }
}
