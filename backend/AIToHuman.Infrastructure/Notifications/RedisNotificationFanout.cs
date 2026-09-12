using System.Text.Json;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Settings;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AIToHuman.Infrastructure.Notifications;

/// <summary>
/// 基于 Redis 发布/订阅的通知扇出：认领到通知的实例把消息广播到频道，所有实例（含发布者自己）收到后
/// 推给本机在线客户端。这样无论哪个实例产生通知、用户连在哪个实例上，都能实时收到。
///
/// 频道名固定：如果允许各实例配不同频道，发布方和订阅方一旦配置不一致，通知会被认领后再也推不出去。
/// 需要隔离环境请用连接串里的库号或键前缀，而不是改频道。
///
/// 未配置连接串、运营未打开开关、或 Redis 不可用时，<see cref="Enabled"/> 为 false，派发方退化为单实例直接推送。
/// </summary>
public sealed class RedisNotificationFanout : INotificationFanout, IAsyncDisposable
{
    /// <summary>扇出频道名；所有实例必须一致，因此不做成运营可改的配置项。</summary>
    public const string Channel = "aitohuman:notifications:fanout";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ISettingsProvider settings;
    private readonly string? connectionString;
    private readonly ILogger<RedisNotificationFanout> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConnectionMultiplexer? connection;
    private bool disposed;

    public RedisNotificationFanout(ISettingsProvider settings, string? connectionString, ILogger<RedisNotificationFanout> logger)
    {
        this.settings = settings;
        this.connectionString = connectionString;
        this.logger = logger;
    }

    public bool Enabled => !disposed && settings.GetBool(SettingKeys.NotificationFanoutEnabled, false) && !string.IsNullOrWhiteSpace(connectionString);

    public async Task PublishAsync(NotificationFanoutMessage message, CancellationToken cancellationToken = default)
    {
        // 派发方是先看 Enabled 再决定走广播的。这里兜住「两次读取之间开关被关掉」的竞态：
        // 静默返回会让这条通知被认领却没人推送；抛出去则派发方撤回认领，下一轮按本地推送重试。
        if (!Enabled) throw new InvalidOperationException("通知扇出未启用，无法广播。");

        var subscriber = (await GetConnectionAsync(cancellationToken)).GetSubscriber();
        var receivers = await subscriber.PublishAsync(RedisChannel.Literal(Channel), JsonSerializer.Serialize(message, SerializerOptions));

        // 广播成功但没有订阅者，说明各实例的订阅都还没建立：通知已经在库里，客户端重连拉取补得上，
        // 但实时推送这一跳丢了，必须让运维在日志里看得见。
        if (receivers == 0)
        {
            logger.LogWarning("通知 {NotificationId} 已广播，但当前没有任何实例在订阅扇出频道 {Channel}。", message.NotificationId, Channel);
        }
    }

    public async Task SubscribeAsync(Func<NotificationFanoutMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        if (!Enabled) return;

        var subscriber = (await GetConnectionAsync(cancellationToken)).GetSubscriber();
        var queue = await subscriber.SubscribeAsync(RedisChannel.Literal(Channel));
        logger.LogInformation("通知扇出已订阅 Redis 频道 {Channel}。", Channel);

        try
        {
            await foreach (var message in queue.WithCancellation(cancellationToken))
            {
                if (Deserialize(message) is not { } notification) continue;

                try
                {
                    await handler(notification, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // 单个客户端推送失败不能让整个订阅断掉：通知已经在库里，客户端重连后仍能从 REST 拉回。
                    logger.LogWarning(exception, "推送扇出通知 {NotificationId} 失败，已跳过。", notification.NotificationId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停机：订阅协程被取消，不算错误。
        }
        finally
        {
            await UnsubscribeAsync(queue);
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        if (connection is not null)
        {
            await connection.DisposeAsync();
            connection = null;
        }

        gate.Dispose();
    }

    /// <summary>懒连接：只有真的启用扇出时才建立连接，避免没配 Redis 的部署在启动时白连一次。</summary>
    private async Task<ConnectionMultiplexer> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (connection is not null) return connection;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (connection is null)
            {
                var options = ConfigurationOptions.Parse(connectionString!);
                // Redis 暂时不可用时保持后台重连，而不是让发布直接抛错、更不能让进程起不来。
                options.AbortOnConnectFail = false;
                // 广播失败时派发方会兜底推本实例客户端，所以这里不能让一条命令卡住整个派发周期：
                // 通知载荷很小，2 秒足够，超时就交给兜底路径。
                options.AsyncTimeout = 2000;
                connection = await ConnectionMultiplexer.ConnectAsync(options);
                logger.LogInformation("通知扇出已创建 Redis 连接（{Endpoints}，连接失败时后台自动重连）。", string.Join(",", options.EndPoints));
            }

            return connection;
        }
        finally
        {
            gate.Release();
        }
    }

    private NotificationFanoutMessage? Deserialize(ChannelMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationFanoutMessage>(message.Message.ToString(), SerializerOptions);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "收到无法解析的通知扇出消息，已忽略。");
            return null;
        }
    }

    private async Task UnsubscribeAsync(ChannelMessageQueue queue)
    {
        try
        {
            await queue.UnsubscribeAsync();
        }
        catch (Exception exception) when (exception is RedisException or ObjectDisposedException or InvalidOperationException)
        {
            logger.LogDebug(exception, "取消通知扇出订阅时出错，连接即将释放，忽略。");
        }
    }
}
