using AIToHuman.Application.Idempotency;
using AIToHuman.Domain.Idempotency;
using AIToHuman.Infrastructure.Idempotency;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 幂等记录清理：已完成的按保留时长过期、未完成的占位按更短的时长过期、单次有批量上限。
/// </summary>
public sealed class IdempotencyCleanupTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void Sweeping_removes_only_the_expired_records()
    {
        var world = new World();
        var expired = world.Complete("old", Now - IdempotencyCleanup.Retention - TimeSpan.FromMinutes(1));
        var fresh = world.Complete("fresh", Now - TimeSpan.FromMinutes(5));
        var stalePlaceholder = world.Placeholder("stale", Now - IdempotencyCleanup.StaleInFlight - TimeSpan.FromMinutes(1));
        var inFlight = world.Placeholder("running", Now - TimeSpan.FromSeconds(30));

        var result = world.Cleanup.Sweep();

        Assert.Equal(2, result.Deleted);
        Assert.Equal(Now, result.SweptAt);
        // 还在客户端重试窗口内的记录不能被删，否则"重试去重"会在最需要的时候失效。
        Assert.NotNull(world.Store.Find(User, "fresh"));
        Assert.NotNull(world.Store.Find(User, "running"));
        Assert.Null(world.Store.Find(User, "old"));
        Assert.Null(world.Store.Find(User, "stale"));
        Assert.NotNull(expired);
        Assert.NotNull(stalePlaceholder);
    }

    [Fact]
    public void Sweeping_respects_the_batch_limit()
    {
        var world = new World();
        for (var index = 0; index < 5; index++)
        {
            world.Complete($"key-{index}", Now - IdempotencyCleanup.Retention - TimeSpan.FromHours(1));
        }

        var first = world.Cleanup.Sweep(batch: 2);
        var second = world.Cleanup.Sweep(batch: 2);
        var third = world.Cleanup.Sweep(batch: 2);

        Assert.Equal(2, first.Deleted);
        Assert.Equal(2, second.Deleted);
        Assert.Equal(1, third.Deleted);
        Assert.Equal(0, world.Cleanup.Sweep().Deleted);
    }

    [Fact]
    public void An_empty_sweep_reports_zero()
    {
        var world = new World();

        var result = world.Cleanup.Sweep();

        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void The_policy_is_described_for_operators()
    {
        var description = IdempotencyCleanup.Describe();

        Assert.Contains("24 小时", description);
        Assert.Contains("10 分钟", description);
        Assert.Contains("500", description);
    }

    private sealed class World
    {
        public World()
        {
            Clock = new MutableTimeProvider(Now);
            Store = new InMemoryIdempotencyStore();
            Cleanup = new IdempotencyCleanup(Store, Clock);
        }

        public MutableTimeProvider Clock { get; }
        public InMemoryIdempotencyStore Store { get; }
        public IdempotencyCleanup Cleanup { get; }

        /// <summary>插入一条已完成的记录（完成时刻由调用方指定）。</summary>
        public IdempotencyEntry Complete(string key, DateTimeOffset completedAt)
        {
            var entry = IdempotencyEntry.Start(User, key, new string('a', 64), completedAt - TimeSpan.FromMinutes(1));
            Assert.True(Store.TryStart(entry, out _));
            Store.Complete(entry, 200, "{}", "application/json", completedAt);
            return entry;
        }

        /// <summary>插入一条只有占位的记录（模拟"执行中途挂了"）。</summary>
        public IdempotencyEntry Placeholder(string key, DateTimeOffset startedAt)
        {
            var entry = IdempotencyEntry.Start(User, key, new string('b', 64), startedAt);
            Assert.True(Store.TryStart(entry, out _));
            return entry;
        }
    }
}
