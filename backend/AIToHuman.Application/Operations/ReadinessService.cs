using AIToHuman.Application.Settings;

namespace AIToHuman.Application.Operations;

/// <summary>单项依赖探测的结论。三态是刻意的：<see cref="Skipped"/> 表示"这个能力当前没开，不参与判定"。</summary>
public enum ReadinessCheckStatus
{
    /// <summary>依赖可用。</summary>
    Ok,

    /// <summary>依赖不可用：整体就绪结论为不健康。</summary>
    Failed,

    /// <summary>当前配置下这个依赖用不上（例如没开多实例扇出时的 Redis），不影响就绪结论。</summary>
    Skipped
}

/// <summary>单项依赖探测的结果。耗时一并返回，运维看日志时能分辨"连不上"和"连得很慢"。</summary>
public sealed record ReadinessCheckResult(
    string Name,
    ReadinessCheckStatus Status,
    string Detail,
    long DurationMs)
{
    public static ReadinessCheckResult Ok(string name, string detail, long durationMs) =>
        new(name, ReadinessCheckStatus.Ok, detail, durationMs);

    public static ReadinessCheckResult Failed(string name, string detail, long durationMs) =>
        new(name, ReadinessCheckStatus.Failed, detail, durationMs);

    public static ReadinessCheckResult Skipped(string name, string detail) =>
        new(name, ReadinessCheckStatus.Skipped, detail, 0);
}

/// <summary>
/// 一项依赖的自检。实现必须自己处理超时与异常：探测出问题时返回 <see cref="ReadinessCheckResult.Failed"/>，
/// 不要把异常抛给调用方——就绪端点自己挂掉是最没用的监控信号。
/// </summary>
public interface IReadinessCheck
{
    /// <summary>探测项的名称（出现在响应里，用英文短名，例如 <c>postgres</c>）。</summary>
    string Name { get; }

    Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>整体就绪结论：只要有一项 <see cref="ReadinessCheckStatus.Failed"/> 就不健康。</summary>
public sealed record ReadinessReport(
    bool Healthy,
    DateTimeOffset CheckedAt,
    long DurationMs,
    IReadOnlyList<ReadinessCheckResult> Checks);

/// <summary>
/// 依赖感知的就绪检查（给编排系统与运维用）。
///
/// 与 <c>/health</c> 的分工是刻意的：
/// - <c>/health</c> 是**存活**探针，只说明"进程还在跑"，不碰数据库、Redis 与对象存储。依赖挂了不该让编排系统重启进程
///   （重启解决不了数据库宕机，只会让所有实例一起抖动）。
/// - <c>/health/ready</c> 是**就绪**探针，逐项探测依赖，任一不健康返回 503，编排系统据此把流量摘掉。
///
/// 探测并行执行、单项各自超时；任何异常都被吃掉并记成该项失败，因此这个端点不会因为依赖炸掉而返回 500。
/// </summary>
public sealed class ReadinessService(IEnumerable<IReadinessCheck> checks, TimeProvider clock)
{
    /// <summary>按当前配置解析出生效的单项超时（秒）。</summary>
    public static int ResolveTimeoutSeconds(ISettingsProvider settings)
    {
        var configured = settings.GetInt(SettingKeys.ReadinessTimeoutSeconds);
        return Math.Clamp(configured ?? 3, 1, 30);
    }

    public async Task<ReadinessReport> CheckAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow();
        var startedTicks = TimeProvider.System.GetTimestamp();

        var results = await Task.WhenAll(checks.Select(check => RunAsync(check, timeoutSeconds, cancellationToken)))
            .ConfigureAwait(false);

        var elapsedMs = (long)TimeProvider.System.GetElapsedTime(startedTicks).TotalMilliseconds;
        var healthy = results.All(result => result.Status != ReadinessCheckStatus.Failed);
        return new ReadinessReport(healthy, startedAt, elapsedMs, results);
    }

    private static async Task<ReadinessCheckResult> RunAsync(
        IReadinessCheck check,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var startedTicks = TimeProvider.System.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var timeoutMessage = ReadinessCheckResult.Failed(
            check.Name,
            $"探测超过 {timeoutSeconds} 秒没有返回，按不健康处理。",
            Elapsed(startedTicks));

        try
        {
            var probe = check.CheckAsync(timeout.Token);

            // 只靠取消令牌是不够的：第三方客户端里有的调用根本不接受令牌（例如 StackExchange.Redis 的
            // ConnectAsync 就没有重载带 CancellationToken），第一次建立连接慢的时候能拖到十几秒。
            // 就绪端点必须自己给出上限，否则编排系统看到的"未响应"其实是它自己的超时，排查时会误判。
            var finished = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken))
                .ConfigureAwait(false);
            if (finished != probe) return timeoutMessage;

            return await probe.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return timeoutMessage;
        }
        catch (Exception exception)
        {
            // 探测本身抛异常（例如连接串格式错、凭据被拒）：报告失败，但绝不让就绪端点跟着 500。
            return ReadinessCheckResult.Failed(check.Name, $"{exception.GetType().Name}：{exception.Message}", Elapsed(startedTicks));
        }
    }

    private static long Elapsed(long startedTicks) =>
        (long)TimeProvider.System.GetElapsedTime(startedTicks).TotalMilliseconds;
}
