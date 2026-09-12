using AIToHuman.Application.Orders;

namespace AIToHuman.Api.Orders;

/// <summary>
/// 待扫描凭证的自动重扫。当扫描服务不可用且 <c>evidence.scanner.failMode=closed</c> 时，
/// 上传的凭证会停在“待扫描、不可下载”，这里按固定周期重试：先退避 30 秒、最多
/// <see cref="AIToHuman.Domain.Orders.OrderEvidence.MaxScanAttempts"/> 次，直到给出结论；
/// 用完次数后保留待扫描状态并写清原因，交给人工处理，而不是无声无息地永远挂着。
/// </summary>
public sealed class EvidenceRescanService(IServiceScopeFactory scopeFactory, ILogger<EvidenceRescanService> logger) : BackgroundService
{
    /// <summary>轮询周期。真正会不会重试还取决于每条凭证自己的退避与尝试次数。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handled = await scope.ServiceProvider.GetRequiredService<EvidenceService>().RescanPendingAsync(stoppingToken);
                if (handled > 0) logger.LogInformation("重新扫描了 {Count} 份待检查的凭证。", handled);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // 一轮失败不能影响服务本身：等下一轮再来。
                logger.LogError(exception, "重新扫描待检查凭证失败，等下一轮再试。");
            }
        }
    }
}
