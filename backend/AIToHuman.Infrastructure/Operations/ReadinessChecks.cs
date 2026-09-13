using AIToHuman.Application.Notifications;
using AIToHuman.Application.Operations;
using AIToHuman.Application.Settings;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Operations;

/// <summary>
/// PostgreSQL 自检：能不能连上、迁移有没有全部应用。
/// 只看"能连上"是不够的——库连得上但少一个迁移时，接口照样会在运行时炸，所以顺便读一次迁移历史。
/// </summary>
public sealed class PostgresReadinessCheck(TaskDbContext db) : IReadinessCheck
{
    public string Name => "postgres";

    public async Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var startedTicks = TimeProvider.System.GetTimestamp();
        if (!await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
        {
            return ReadinessCheckResult.Failed(Name, "连不上 PostgreSQL：请检查服务是否在运行、连接串与凭据是否正确。", Elapsed(startedTicks));
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count();
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Count();
        if (pending > 0)
        {
            return ReadinessCheckResult.Failed(
                Name,
                $"库连得上，但有 {pending} 个迁移没有应用（已应用 {applied} 个）：请先完成迁移再放流量进来。",
                Elapsed(startedTicks));
        }

        return ReadinessCheckResult.Ok(Name, $"连接正常，已应用的迁移 {applied} 个，没有待应用的迁移。", Elapsed(startedTicks));
    }

    private static long Elapsed(long startedTicks) =>
        (long)TimeProvider.System.GetElapsedTime(startedTicks).TotalMilliseconds;
}

/// <summary>
/// Redis 自检：只在开了多实例通知扇出时才参与判定（没开扇出时 Redis 用不上，报"跳过"而不是"失败"，
/// 否则单实例部署会因为没配 Redis 而永远"未就绪"）。探测走扇出组件自己的连接（<see cref="INotificationFanoutProbe"/>），
/// 不额外新建连接。
/// </summary>
public sealed class RedisReadinessCheck(
    ISettingsProvider settings,
    INotificationFanout fanout) : IReadinessCheck
{
    public string Name => "redis";

    public async Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!settings.GetBool(SettingKeys.NotificationFanoutEnabled, false))
        {
            return ReadinessCheckResult.Skipped(Name, "没有开启多实例通知扇出，本次不探测 Redis。");
        }

        // 开关开着但扇出仍不可用，几乎只有一个原因：部署配置里没有连接串。
        if (!fanout.Enabled)
        {
            return ReadinessCheckResult.Failed(
                Name,
                "已开启多实例扇出，但扇出未就绪：请补上 ConnectionStrings__Redis，或把 notifications.fanout.enabled 关掉。",
                0);
        }

        if (fanout is not INotificationFanoutProbe probe)
        {
            return ReadinessCheckResult.Skipped(Name, "当前扇出实现不支持连通性探测。");
        }

        var startedTicks = TimeProvider.System.GetTimestamp();
        try
        {
            var detail = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
            return ReadinessCheckResult.Ok(Name, $"连接正常，{detail}", Elapsed(startedTicks));
        }
        catch (Exception exception)
        {
            return ReadinessCheckResult.Failed(Name, $"连不上 Redis：{exception.Message}", Elapsed(startedTicks));
        }
    }

    private static long Elapsed(long startedTicks) =>
        (long)TimeProvider.System.GetElapsedTime(startedTicks).TotalMilliseconds;
}

/// <summary>
/// 文件存储自检：本机目录**写一条探针对象再删掉**（能捕获"目录不存在/没权限/磁盘满"，只查存在性做不到），
/// S3 兼容存储发一个存在性探测（返回"不存在"也是成功——探测键本来就没有，关键是请求发得出去、
/// 服务端认这份签名与凭据；缺配置、桶不存在、密钥被拒都会抛错并被记成不健康）。
/// </summary>
public sealed class StorageReadinessCheck(
    ISettingsProvider settings,
    AIToHuman.Application.Orders.IFileStorage storage) : IReadinessCheck
{
    /// <summary>探测用的对象键：固定不变、不会被真实业务读写，每次探测后都会删掉。</summary>
    public const string ProbeKey = "readiness/probe.txt";

    public string Name => "storage";

    public async Task<ReadinessCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var provider = settings.GetChoice(SettingKeys.StorageProvider, "local");
        var startedTicks = TimeProvider.System.GetTimestamp();

        try
        {
            if (provider == "local")
            {
                using var content = new MemoryStream("readiness"u8.ToArray());
                await storage.SaveAsync(ProbeKey, content, "text/plain", cancellationToken).ConfigureAwait(false);
                await storage.DeleteAsync(ProbeKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await storage.ExistsAsync(ProbeKey, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            // 缺配置、桶不存在、密钥无权限、目录不可写都会走到这里；异常文案本身就是可行动的排查线索。
            return ReadinessCheckResult.Failed(Name, $"{provider} 存储不可用：{exception.Message}", Elapsed(startedTicks));
        }

        var detail = provider == "local"
            ? $"本机目录可读写（写探测对象 {ProbeKey} 后已删除）。"
            : $"{provider} 存储可访问（探测键 {ProbeKey}）。";

        return ReadinessCheckResult.Ok(Name, detail, Elapsed(startedTicks));
    }

    private static long Elapsed(long startedTicks) =>
        (long)TimeProvider.System.GetElapsedTime(startedTicks).TotalMilliseconds;
}
