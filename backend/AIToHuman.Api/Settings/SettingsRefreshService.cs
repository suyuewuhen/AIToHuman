using AIToHuman.Application.Settings;

namespace AIToHuman.Api.Settings;

/// <summary>
/// 刷新内存快照：运营后台保存成功后立即调用一次，新值马上生效（不需要重启进程）。
/// 刷新失败不会把请求打挂——保留上一份快照并记日志，下一次写入或后台轮询会再试。
/// </summary>
public sealed class SettingsSnapshotReloader(
    IServiceScopeFactory scopeFactory,
    SettingsCache cache,
    ILogger<SettingsSnapshotReloader> logger) : ISettingsReloader
{
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Reload();
        return Task.CompletedTask;
    }

    public void Reload()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var snapshot = scope.ServiceProvider.GetRequiredService<SettingsSnapshotBuilder>().Build();
            var previous = cache.Current;
            cache.Replace(snapshot);
            if (previous.Count != snapshot.Count || !SameValues(previous, snapshot))
                logger.LogInformation("运营配置快照已刷新，共 {Count} 项。", snapshot.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "重新加载运营配置失败，继续使用上一份快照。");
        }
    }

    private static bool SameValues(SettingsSnapshot left, SettingsSnapshot right)
    {
        if (left.Count != right.Count) return false;
        foreach (var (key, entry) in left.Entries)
        {
            if (!right.Entries.TryGetValue(key, out var other) || other.Value != entry.Value) return false;
        }

        return true;
    }
}

/// <summary>
/// 后台轮询：有人直接改数据库、或另一个实例改过配置时，本实例也能在 15 秒内跟上。
/// 多实例只要共享同一个数据库就有效，不需要额外中间件。
/// </summary>
public sealed class SettingsRefreshService(SettingsSnapshotReloader reloader) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            reloader.Reload();
        }
    }
}

/// <summary>启动时初始化快照：数据库不可用时先只用环境变量与默认值，后台轮询会继续重试。</summary>
public static class SettingsStartup
{
    public static void Initialize(IServiceProvider services, ILogger logger)
    {
        var cache = services.GetRequiredService<SettingsCache>();
        using var scope = services.CreateScope();
        var builder = scope.ServiceProvider.GetRequiredService<SettingsSnapshotBuilder>();

        try
        {
            cache.Replace(builder.Build());
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "读取数据库里的运营配置失败，先使用环境变量与代码默认值；后台任务会继续重试。");
            cache.Replace(builder.Build(includeDatabase: false));
        }
    }
}
