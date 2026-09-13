using System.Collections.Concurrent;
using AIToHuman.Application.Settings;

namespace AIToHuman.Application.Operations;

/// <summary>当前生效的限流口径（每次请求现读设置，因此运营改完立即生效）。</summary>
public sealed record RateLimitPolicy(bool Enabled, int WritesPerMinute)
{
    /// <summary>没配置过上限时使用的默认值：足够宽，只挡脚本刷接口。</summary>
    public const int DefaultWritesPerMinute = 240;

    public static RateLimitPolicy Resolve(ISettingsProvider settings) => new(
        settings.GetBool(SettingKeys.RateLimitEnabled, true),
        Math.Clamp(
            settings.GetInt(SettingKeys.RateLimitWritesPerMinute) ?? DefaultWritesPerMinute,
            1,
            100_000));
}

/// <summary>一次限流判定的结论。被拒时带上还要等多少秒，供 <c>Retry-After</c> 使用。</summary>
public sealed record RateLimitDecision(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds)
{
    public static RateLimitDecision Unconstrained(int limit) => new(true, limit, limit, 0);
}

/// <summary>
/// 写接口的固定窗口限流器（内存计数）。
///
/// 口径与取舍：
/// - **只限写请求**（POST/PUT/PATCH/DELETE），读接口不受影响：限流的目标是挡住脚本反复创建/加价/上传，
///   而不是让用户刷不出大厅。
/// - **分区键由调用方给**：已登录用用户 id、未登录用来源 IP。未登录请求（注册/登录）也必须限，
///   否则暴力撞库没有成本。
/// - **固定一分钟窗口**（对齐 UTC 整分钟），不是滑动窗口：实现简单、可解释，运营与用户都能看明白
///   "这一分钟里的第 N 个写请求被挡住了"；代价是窗口边界处最多容许两倍瞬时速率，对本场景足够。
/// - **计数是进程内的**：多实例部署时每个实例各记一份，实际配额是"每实例 × 上限"。
///   真正的跨实例配额需要 Redis 计数器，属于后续工作（见交接文档）。
/// - 关掉开关（<c>ratelimit.enabled=false</c>）时完全不做计数，作为抢修时的一键降级。
/// </summary>
public sealed class WriteRateLimiter(ISettingsProvider settings, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);
    private long lastPrunedMinute = -1;

    /// <summary>当前还在计数里的窗口数（测试与运维排查用）。</summary>
    public int ActiveWindowCount => windows.Count;

    public RateLimitDecision Evaluate(string partitionKey)
    {
        var policy = RateLimitPolicy.Resolve(settings);
        if (!policy.Enabled) return RateLimitDecision.Unconstrained(policy.WritesPerMinute);

        var now = clock.GetUtcNow();
        var minute = now.ToUnixTimeSeconds() / 60;
        Prune(minute);

        var window = windows.AddOrUpdate(
            partitionKey,
            _ => new Window(minute, 1),
            (_, existing) => existing.Minute == minute ? existing with { Count = existing.Count + 1 } : new Window(minute, 1));

        var remaining = Math.Max(0, policy.WritesPerMinute - window.Count);
        if (window.Count <= policy.WritesPerMinute)
        {
            return new RateLimitDecision(true, policy.WritesPerMinute, remaining, 0);
        }

        var secondsLeft = (minute + 1) * 60 - now.ToUnixTimeSeconds();
        return new RateLimitDecision(false, policy.WritesPerMinute, 0, (int)Math.Max(1, secondsLeft));
    }

    /// <summary>整分钟切换时清掉上一个窗口的计数：老窗口已经没有任何判定价值，留着只会让字典无限长大。</summary>
    private void Prune(long minute)
    {
        if (Interlocked.Exchange(ref lastPrunedMinute, minute) == minute) return;

        foreach (var item in windows)
        {
            if (item.Value.Minute < minute) windows.TryRemove(item.Key, out _);
        }
    }

    private readonly record struct Window(long Minute, int Count);
}
