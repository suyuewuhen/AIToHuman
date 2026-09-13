using AIToHuman.Application.Risk;

namespace AIToHuman.Api.Risk;

/// <summary>
/// 发布后风险复检的后台任务：运营改了规则目录之后，仍然在线的任务还是按旧版规则判定过的，
/// 这里周期性地把它们重新判一遍（命中禁止类别就下架/冻结订单，命中转人工就要求复检）。
///
/// 为什么要周期扫而不是"改规则时立刻扫一遍"：
/// 前者天然覆盖各种"任务在改规则之后才变成在线"的顺序问题（例如改规则时这条任务还在草稿里，
/// 之后才发布），也不要求改规则这个请求去背一次全表扫描。改规则的响应因此很快，
/// 运营需要立即复检时也可以手动触发（<c>POST /api/v1/admin/risk/recheck</c>）。
/// </summary>
public sealed class RiskRecheckService(IServiceScopeFactory scopeFactory, ILogger<RiskRecheckService> logger) : BackgroundService
{
    /// <summary>扫描周期。规则改动不是秒级敏感，五分钟一轮足够，也不会给数据库造成压力。</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>启动后先等一会儿再扫第一轮：让应用先完成启动与迁移，避免和冷启动抢资源。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SweepAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SweepAsync(stoppingToken);
        }
    }

    /// <summary>跑一轮复检。同步执行（一轮就是一次数据库批处理），因此这里不需要 async。</summary>
    private Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var result = scope.ServiceProvider.GetRequiredService<RiskEnforcementService>().Recheck();
            if (result.Scanned == 0) logger.LogDebug("发布后风险复检：当前没有需要复检的在线任务（规则第 {Version} 版）。", result.RuleVersion);

            if (result.Unpublished > 0 || result.Frozen > 0 || result.Flagged > 0)
            {
                logger.LogWarning(
                    "发布后风险复检（规则第 {Version} 版）：扫描 {Scanned} 条，自动下架 {Unpublished} 条、冻结订单 {Frozen} 条、要求人工复检 {Flagged} 条。",
                    result.RuleVersion, result.Scanned, result.Unpublished, result.Frozen, result.Flagged);
            }

            if (result.Skipped > 0) logger.LogDebug("{Count} 条候选在扫描期间不满足处置条件（订单状态不可冻结等），留给下一轮。", result.Skipped);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停机。
        }
        catch (Exception exception)
        {
            // 一轮失败不影响服务本身：等下一轮再来。
            logger.LogError(exception, "发布后风险复检失败，等下一轮再试。");
        }

        return Task.CompletedTask;
    }
}
