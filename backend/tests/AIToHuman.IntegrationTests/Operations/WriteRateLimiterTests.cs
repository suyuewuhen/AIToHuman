using AIToHuman.Application.Operations;
using AIToHuman.Application.Settings;

namespace AIToHuman.IntegrationTests.Operations;

/// <summary>
/// 写接口限流的口径：固定一分钟窗口、按分区（用户/IP）独立计数、超限给出 Retry-After，
/// 关掉开关时完全不计数。这些行为决定了"脚本刷接口会被挡住，而正常用户不会被误伤"。
/// </summary>
public sealed class WriteRateLimiterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 30, TimeSpan.Zero);

    [Fact]
    public void Requests_up_to_the_limit_are_allowed_and_the_next_one_is_rejected()
    {
        var limiter = Build(limit: 3);

        Assert.True(limiter.Evaluate("user:a").Allowed);
        Assert.True(limiter.Evaluate("user:a").Allowed);
        var third = limiter.Evaluate("user:a");
        Assert.True(third.Allowed);
        Assert.Equal(0, third.Remaining);

        var fourth = limiter.Evaluate("user:a");
        Assert.False(fourth.Allowed);
        Assert.Equal(3, fourth.Limit);
        // 窗口是整分钟对齐的：8:00:30 提交，8:01:00 才能再来，因此还要等 30 秒。
        Assert.Equal(30, fourth.RetryAfterSeconds);
    }

    [Fact]
    public void Partitions_are_counted_independently()
    {
        var limiter = Build(limit: 1);

        Assert.True(limiter.Evaluate("user:a").Allowed);
        // 另一个人（或另一个 IP）不该被别人的用量牵连。
        Assert.True(limiter.Evaluate("user:b").Allowed);
        Assert.True(limiter.Evaluate("ip:127.0.0.1").Allowed);
        Assert.False(limiter.Evaluate("user:a").Allowed);
    }

    [Fact]
    public void A_new_minute_window_resets_the_counter_and_drops_the_old_one()
    {
        var clock = new MutableTimeProvider(Now);
        var limiter = Build(limit: 1, clock: clock);

        Assert.True(limiter.Evaluate("user:a").Allowed);
        Assert.False(limiter.Evaluate("user:a").Allowed);
        Assert.Equal(1, limiter.ActiveWindowCount);

        // 跨到下一分钟：计数重新开始，且上一个窗口被清掉（否则字典会随用户数无限增长）。
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(limiter.Evaluate("user:a").Allowed);
        Assert.Equal(1, limiter.ActiveWindowCount);
    }

    [Fact]
    public void Disabling_the_switch_stops_counting_entirely()
    {
        var settings = new TestSettingsProvider(new Dictionary<string, string?>
        {
            [SettingKeys.RateLimitEnabled] = "false",
            [SettingKeys.RateLimitWritesPerMinute] = "1"
        });
        var limiter = new WriteRateLimiter(settings, new MutableTimeProvider(Now));

        // 关掉开关时连计数都不做：抢修期间"把限流关掉"必须是真正的一键降级。
        for (var i = 0; i < 10; i++) Assert.True(limiter.Evaluate("user:a").Allowed);
        Assert.Equal(0, limiter.ActiveWindowCount);
    }

    [Fact]
    public void Missing_or_broken_settings_fall_back_to_the_documented_defaults()
    {
        var empty = new WriteRateLimiter(new TestSettingsProvider(new Dictionary<string, string?>()), new MutableTimeProvider(Now));
        var decision = empty.Evaluate("user:a");
        Assert.True(decision.Allowed);
        Assert.Equal(RateLimitPolicy.DefaultWritesPerMinute, decision.Limit);

        // 值不是数字（运营填错、被别的系统写坏）时退回默认值：限流不该因为一个坏配置把所有人挡住。
        var broken = new WriteRateLimiter(
            new TestSettingsProvider(new Dictionary<string, string?> { [SettingKeys.RateLimitWritesPerMinute] = "很快" }),
            new MutableTimeProvider(Now));
        Assert.Equal(RateLimitPolicy.DefaultWritesPerMinute, broken.Evaluate("user:a").Limit);

        // 低于 1 的上限没有意义，夹到 1（仍然允许第一个请求）。
        var zero = new WriteRateLimiter(
            new TestSettingsProvider(new Dictionary<string, string?> { [SettingKeys.RateLimitWritesPerMinute] = "0" }),
            new MutableTimeProvider(Now));
        var first = zero.Evaluate("user:a");
        Assert.True(first.Allowed);
        Assert.Equal(1, first.Limit);
        Assert.False(zero.Evaluate("user:a").Allowed);
    }

    [Fact]
    public void Hot_reload_takes_effect_on_the_next_request()
    {
        var settings = new MutableSettingsProvider(new Dictionary<string, string?>
        {
            [SettingKeys.RateLimitWritesPerMinute] = "5"
        });
        var limiter = new WriteRateLimiter(settings, new MutableTimeProvider(Now));
        Assert.Equal(5, limiter.Evaluate("user:a").Limit);

        // 运营在后台改成 1：下一个请求就该按新口径判定（限流器每次现读设置，不缓存策略）。
        settings.Set(SettingKeys.RateLimitWritesPerMinute, "1");
        Assert.False(limiter.Evaluate("user:a").Allowed);
    }

    private static WriteRateLimiter Build(int limit, MutableTimeProvider? clock = null) =>
        new(
            new TestSettingsProvider(new Dictionary<string, string?>
            {
                [SettingKeys.RateLimitEnabled] = "true",
                [SettingKeys.RateLimitWritesPerMinute] = limit.ToString()
            }),
            clock ?? new MutableTimeProvider(Now));

    /// <summary>可改的设置提供者：用来验证"运营改完立即生效"。</summary>
    private sealed class MutableSettingsProvider(Dictionary<string, string?> values) : ISettingsProvider
    {
        public string? GetValue(string key) => values.TryGetValue(key, out var value) ? value : null;

        public SettingSource GetSource(string key) => values.ContainsKey(key) ? SettingSource.Database : SettingSource.Default;

        public void Set(string key, string value) => values[key] = value;
    }
}
