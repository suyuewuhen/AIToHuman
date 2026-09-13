namespace AIToHuman.Application.Idempotency;

/// <summary>一次清理的结果：删了多少条、什么时候扫的。</summary>
public sealed record IdempotencyCleanupResult(int Deleted, DateTimeOffset SweptAt);

/// <summary>
/// 幂等记录的定期清理。
///
/// 为什么需要：`idempotency_entries` 是一条一条只增不减的——每次带键的写请求都会留下一条记录，
/// 不清就会一直涨。客户端的重试窗口只有几秒到几分钟，所以保留一天足够覆盖"超时后重试"这个真实场景，
/// 再久的记录只会占空间。
///
/// 两档判定：已完成的按 <see cref="Retention"/> 过期；还没完成的占位（上次执行中途挂了留下的）
/// 按 <see cref="StaleInFlight"/> 过期——后者比领域层的占位超时更宽松，避免刚占位就被清掉。
/// </summary>
public sealed class IdempotencyCleanup(IIdempotencyStore store, TimeProvider timeProvider)
{
    /// <summary>已完成记录的保留时长。</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>未完成占位的保留时长。</summary>
    public static readonly TimeSpan StaleInFlight = TimeSpan.FromMinutes(10);

    /// <summary>单次最多删多少条，避免一次清空把数据库拖住。</summary>
    public const int MaxBatch = 500;

    public IdempotencyCleanupResult Sweep(int? batch = null)
    {
        var now = timeProvider.GetUtcNow();
        var limit = Math.Clamp(batch ?? MaxBatch, 1, MaxBatch);
        var deleted = store.DeleteExpired(now - Retention, now - StaleInFlight, limit);
        return new IdempotencyCleanupResult(deleted, now);
    }

    /// <summary>给运维看的策略说明（日志与文档用同一份口径）。</summary>
    public static string Describe() =>
        $"已完成记录保留 {Retention.TotalHours:0} 小时、未完成占位保留 {StaleInFlight.TotalMinutes:0} 分钟，单次最多清理 {MaxBatch} 条。";
}
