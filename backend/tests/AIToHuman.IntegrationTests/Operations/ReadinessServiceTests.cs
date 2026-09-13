using AIToHuman.Application.Operations;
using AIToHuman.Application.Settings;

namespace AIToHuman.IntegrationTests.Operations;

/// <summary>
/// 就绪检查的聚合语义：任一依赖失败即整体不健康、跳过不算失败、
/// 探测抛异常或超时都要被翻译成"这一项不健康"而不是让端点自己 500。
/// </summary>
public sealed class ReadinessServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task All_checks_passing_makes_the_report_healthy()
    {
        var service = new ReadinessService(
            [
                new StubCheck("postgres", ReadinessCheckResult.Ok("postgres", "连接正常。", 3)),
                new StubCheck("storage", ReadinessCheckResult.Ok("storage", "本机目录可读写。", 1))
            ],
            new FixedTimeProvider(Now));

        var report = await service.CheckAsync(timeoutSeconds: 3, CancellationToken.None);

        Assert.True(report.Healthy);
        Assert.Equal(Now, report.CheckedAt);
        Assert.Equal(2, report.Checks.Count);
        Assert.All(report.Checks, check => Assert.Equal(ReadinessCheckStatus.Ok, check.Status));
    }

    [Fact]
    public async Task Any_failing_check_makes_the_report_unhealthy_and_keeps_the_reason()
    {
        var service = new ReadinessService(
            [
                new StubCheck("postgres", ReadinessCheckResult.Ok("postgres", "连接正常。", 3)),
                new StubCheck("redis", ReadinessCheckResult.Failed("redis", "连不上 Redis：连接被拒绝。", 12))
            ],
            new FixedTimeProvider(Now));

        var report = await service.CheckAsync(timeoutSeconds: 3, CancellationToken.None);

        Assert.False(report.Healthy);
        // 不健康的项要连原因一起回传：运维只看"unhealthy"没法排查。
        var failed = Assert.Single(report.Checks, check => check.Status == ReadinessCheckStatus.Failed);
        Assert.Equal("redis", failed.Name);
        Assert.Contains("连接被拒绝", failed.Detail);
    }

    [Fact]
    public async Task A_skipped_check_does_not_make_the_report_unhealthy()
    {
        var service = new ReadinessService(
            [
                new StubCheck("postgres", ReadinessCheckResult.Ok("postgres", "连接正常。", 3)),
                new StubCheck("redis", ReadinessCheckResult.Skipped("redis", "没有开启多实例通知扇出，本次不探测 Redis。"))
            ],
            new FixedTimeProvider(Now));

        var report = await service.CheckAsync(timeoutSeconds: 3, CancellationToken.None);

        Assert.True(report.Healthy);
        Assert.Equal(ReadinessCheckStatus.Skipped, report.Checks.Single(check => check.Name == "redis").Status);
    }

    [Fact]
    public async Task A_throwing_check_becomes_a_failure_instead_of_a_500()
    {
        var service = new ReadinessService([new ThrowingCheck("storage", new InvalidOperationException("对象存储还没有配置完整：请补齐 storage.s3.bucket。"))], new FixedTimeProvider(Now));

        var report = await service.CheckAsync(timeoutSeconds: 3, CancellationToken.None);

        Assert.False(report.Healthy);
        var check = Assert.Single(report.Checks);
        Assert.Equal(ReadinessCheckStatus.Failed, check.Status);
        // 原始异常会带上内部类型名，排查时能分辨"配置缺项"和"服务拒绝"。
        Assert.Contains("InvalidOperationException", check.Detail);
        Assert.Contains("storage.s3.bucket", check.Detail);
    }

    [Fact]
    public async Task A_hanging_check_is_reported_as_a_timeout()
    {
        var service = new ReadinessService([new HangingCheck("postgres")], new FixedTimeProvider(Now));

        var report = await service.CheckAsync(timeoutSeconds: 1, CancellationToken.None);

        Assert.False(report.Healthy);
        var check = Assert.Single(report.Checks);
        Assert.Equal(ReadinessCheckStatus.Failed, check.Status);
        Assert.Contains("超过 1 秒没有返回", check.Detail);
    }

    [Fact]
    public async Task A_check_that_ignores_cancellation_is_still_capped_by_the_timeout()
    {
        // StackExchange.Redis 的 ConnectAsync 就没有带取消令牌的重载：第一次建连慢的时候会拖很久。
        // 就绪端点必须自己封顶，否则运维看到的"未响应"是编排系统自己的超时，排查时会误判成端点挂了。
        var service = new ReadinessService([new UncancelableCheck("redis")], new FixedTimeProvider(Now));

        var startedTicks = TimeProvider.System.GetTimestamp();
        var report = await service.CheckAsync(timeoutSeconds: 1, CancellationToken.None);
        var elapsed = TimeProvider.System.GetElapsedTime(startedTicks);

        Assert.False(report.Healthy);
        Assert.Contains("超过 1 秒没有返回", Assert.Single(report.Checks).Detail);
        // 真的在 1 秒左右返回，而不是等那个 30 秒的探测跑完。
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"就绪检查应该被超时封顶，实际耗时 {elapsed.TotalSeconds:F1} 秒");
    }

    [Fact]
    public void Timeout_setting_is_clamped_and_defaults_to_three_seconds()
    {
        Assert.Equal(3, ReadinessService.ResolveTimeoutSeconds(new TestSettingsProvider(new Dictionary<string, string?>())));
        Assert.Equal(1, ReadinessService.ResolveTimeoutSeconds(Provider("0")));
        Assert.Equal(30, ReadinessService.ResolveTimeoutSeconds(Provider("999")));
        Assert.Equal(7, ReadinessService.ResolveTimeoutSeconds(Provider("7")));

        static TestSettingsProvider Provider(string value) =>
            new(new Dictionary<string, string?> { [SettingKeys.ReadinessTimeoutSeconds] = value });
    }

    private sealed class StubCheck(string name, ReadinessCheckResult result) : IReadinessCheck
    {
        public string Name => name;

        public Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ThrowingCheck(string name, Exception exception) : IReadinessCheck
    {
        public string Name => name;

        public Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken) => throw exception;
    }

    private sealed class HangingCheck(string name) : IReadinessCheck
    {
        public string Name => name;

        public async Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            // 模拟"依赖不响应"：只有被取消才会结束，用来验证单项超时。
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return ReadinessCheckResult.Ok(name, "永远不会到这里。", 0);
        }
    }

    /// <summary>模拟根本不理会取消令牌的探测实现（真实例子：StackExchange.Redis 的 ConnectAsync）。</summary>
    private sealed class UncancelableCheck(string name) : IReadinessCheck
    {
        public string Name => name;

        public async Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return ReadinessCheckResult.Ok(name, "30 秒后才返回。", 30_000);
        }
    }
}
