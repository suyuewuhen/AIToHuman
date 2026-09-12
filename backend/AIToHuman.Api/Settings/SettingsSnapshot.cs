using AIToHuman.Application.Settings;

namespace AIToHuman.Api.Settings;

/// <summary>一条生效配置：取值、来源，以及它来自哪条覆盖记录。</summary>
public sealed record SettingsEntry(string Value, SettingSource Source, int Version, Guid? UpdatedBy, DateTimeOffset? UpdatedAt);

/// <summary>配置的内存快照。刷新时整体换新对象，读取端不加锁。</summary>
public sealed class SettingsSnapshot(IReadOnlyDictionary<string, SettingsEntry> entries)
{
    public static readonly SettingsSnapshot Empty = new(new Dictionary<string, SettingsEntry>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, SettingsEntry> Entries { get; } = entries;

    public int Count => Entries.Count;

    public SettingsEntry? Find(string key) => Entries.TryGetValue(key, out var entry) ? entry : null;
}

/// <summary>保存当前配置快照的单例容器。</summary>
public sealed class SettingsCache
{
    private volatile SettingsSnapshot current = SettingsSnapshot.Empty;

    public SettingsSnapshot Current => current;

    public void Replace(SettingsSnapshot snapshot) => current = snapshot;
}

/// <summary>
/// 构建生效配置快照：先铺上环境变量/配置文件与代码默认值，再用数据库覆盖记录盖在上面。
/// 解密失败的机密会被跳过并记录错误，这样坏掉的密钥不会把整个配置读取拖垮。
/// </summary>
public sealed class SettingsSnapshotBuilder(
    ISystemSettingsRepository repository,
    IConfiguration configuration,
    ISecretProtector protector,
    ILogger<SettingsSnapshotBuilder> logger)
{
    public SettingsSnapshot Build(bool includeDatabase = true)
    {
        var entries = new Dictionary<string, SettingsEntry>(StringComparer.Ordinal);

        foreach (var definition in SettingCatalog.All)
        {
            var configured = configuration[definition.ConfigurationKey];
            if (!string.IsNullOrWhiteSpace(configured))
                entries[definition.Key] = new SettingsEntry(configured.Trim(), SettingSource.Configuration, 0, null, null);
            else if (!string.IsNullOrEmpty(definition.DefaultValue))
                entries[definition.Key] = new SettingsEntry(definition.DefaultValue, SettingSource.Default, 0, null, null);
        }

        if (!includeDatabase) return new SettingsSnapshot(entries);

        foreach (var setting in repository.List())
        {
            var definition = SettingCatalog.TryGet(setting.Key);
            if (definition is null)
            {
                logger.LogWarning("配置表里存在未注册的设置键 {Key}，已忽略。", setting.Key);
                continue;
            }

            var value = setting.IsSecret ? protector.Unprotect(setting.Value) : setting.Value;
            if (value is null)
            {
                logger.LogError(
                    "配置 {Key} 的机密内容无法解密（Data Protection 密钥环可能已更换），本次回退到环境变量或默认值；请在运营后台重新保存该密钥。",
                    setting.Key);
                continue;
            }

            entries[definition.Key] = new SettingsEntry(value, SettingSource.Database, setting.Version, setting.UpdatedBy, setting.UpdatedAt);
        }

        return new SettingsSnapshot(entries);
    }
}

/// <summary>生效配置的读取入口：读内存快照，因此在请求热路径上没有数据库开销。</summary>
public sealed class DatabaseSettingsProvider(SettingsCache cache) : ISettingsProvider
{
    public string? GetValue(string key) => cache.Current.Find(key)?.Value;

    public SettingSource GetSource(string key) => cache.Current.Find(key)?.Source ?? SettingSource.Default;
}
