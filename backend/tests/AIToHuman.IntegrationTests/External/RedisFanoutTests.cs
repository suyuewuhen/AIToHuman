using AIToHuman.Application.Notifications;
using AIToHuman.Application.Settings;
using AIToHuman.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// Redis 扇出的真实行为：广播出去的消息能被订阅方收到、处理失败不会中断订阅、
/// 开关关闭时拒绝广播（派发方据此退回单实例推送）、Redis 不可用时快速失败而不是挂住。
/// </summary>
public sealed class RedisFanoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [RedisFact]
    public async Task Publishing_reaches_a_subscriber_with_the_same_payload()
    {
        await using var fanout = CreateFanout(enabled: true);
        var received = new TaskCompletionSource<NotificationFanoutMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var subscription = fanout.SubscribeAsync((message, _) =>
        {
            received.TrySetResult(message);
            return Task.CompletedTask;
        }, lifetime.Token);

        var message = NewMessage();
        await WaitUntilSubscribedAsync();
        await fanout.PublishAsync(message);

        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(message, actual);
        // 载荷保持原样跨进程传递，客户端拿到的 JSON 不会因为重新序列化而漂移。
        Assert.Equal(message.PayloadJson, actual.PayloadJson);

        await lifetime.CancelAsync();
        await subscription;
    }

    [RedisFact]
    public async Task A_handler_that_throws_does_not_break_the_subscription()
    {
        await using var fanout = CreateFanout(enabled: true);
        var secondDelivery = new TaskCompletionSource<NotificationFanoutMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = 0;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var subscription = fanout.SubscribeAsync((message, _) =>
        {
            // 第一条故意抛：推送失败不能让整个订阅断掉（通知已经在库里，客户端重连还能从 REST 拉回）。
            if (Interlocked.Increment(ref deliveries) == 1) throw new InvalidOperationException("模拟推送失败。");
            secondDelivery.TrySetResult(message);
            return Task.CompletedTask;
        }, lifetime.Token);

        await WaitUntilSubscribedAsync();
        await fanout.PublishAsync(NewMessage("first"));
        await fanout.PublishAsync(NewMessage("second"));

        var second = await secondDelivery.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("second", second.Type);

        await lifetime.CancelAsync();
        await subscription;
    }

    [RedisFact]
    public async Task A_disabled_fanout_refuses_to_publish_so_the_dispatcher_can_fall_back()
    {
        await using var fanout = CreateFanout(enabled: false);

        Assert.False(fanout.Enabled);
        // 开关在"检查"和"广播"之间被关掉时也必须抛：静默返回会让通知被认领却没人推送。
        await Assert.ThrowsAsync<InvalidOperationException>(() => fanout.PublishAsync(NewMessage()));
    }

    [RedisFact]
    public async Task Subscribing_while_disabled_returns_immediately()
    {
        await using var fanout = CreateFanout(enabled: false);

        // 未启用时订阅立即返回，不建立连接也不阻塞后台任务。
        await fanout.SubscribeAsync((_, _) => Task.CompletedTask, CancellationToken.None);
    }

    [Fact]
    public async Task An_unreachable_redis_fails_fast_instead_of_hanging_the_dispatcher()
    {
        // 这一条不需要真实 Redis：它验证的是"配了一个连不上的地址时不能把派发周期卡死"。
        await using var fanout = new RedisNotificationFanout(
            new TestSettingsProvider(new Dictionary<string, string?> { [SettingKeys.NotificationFanoutEnabled] = "true" }),
            "127.0.0.1:6399",
            NullLogger<RedisNotificationFanout>.Instance);

        Assert.True(fanout.Enabled);

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<Exception>(() => fanout.PublishAsync(NewMessage()));
        // 断言的是"它会失败并返回"，而不是"它有多快"：ConnectTimeout + AsyncTimeout 在机器繁忙时会有几秒抖动，
        // 真正要守住的是"不能无限挂住把派发周期卡死"，所以给一个宽松但明确的上限。
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(30), "广播失败应当返回并交给派发方兜底，不能把派发周期卡住。");
    }

    private static RedisNotificationFanout CreateFanout(bool enabled) => new(
        new TestSettingsProvider(new Dictionary<string, string?> { [SettingKeys.NotificationFanoutEnabled] = enabled ? "true" : "false" }),
        ExternalTestEnvironment.RedisConnectionString,
        NullLogger<RedisNotificationFanout>.Instance);

    private static NotificationFanoutMessage NewMessage(string type = "order.statusChanged") => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), type, 1, Now, """{"orderId":"abc"}""");

    /// <summary>订阅是异步建立的：先等 Redis 侧真的多了订阅者，再发布，避免"发得比订阅快"造成的偶发失败。</summary>
    private static async Task WaitUntilSubscribedAsync()
    {
        await using var probe = await ConnectionMultiplexer.ConnectAsync(ExternalTestEnvironment.RedisConnectionString!);
        var database = probe.GetDatabase();

        for (var attempt = 0; attempt < 40; attempt++)
        {
            // PUBSUB NUMSUB <channel> 返回 [channel, count]。
            var result = await database.ExecuteAsync("PUBSUB", "NUMSUB", RedisNotificationFanout.Channel);
            if (result.Resp2Type == ResultType.Array && result.Length >= 2 && (long)result[1] > 0) return;

            await Task.Delay(100);
        }

        Assert.Fail("等待订阅建立超时：扇出频道上始终没有订阅者。");
    }
}
