using System.Globalization;

namespace AIToHuman.Application.Settings;

/// <summary>某个生效值来自哪里，用于运营后台展示“这条配置正在被谁覆盖”。</summary>
public enum SettingSource
{
    Default,
    Configuration,
    Database
}

/// <summary>
/// 生效配置的读取入口。解析顺序固定为「数据库覆盖 → 环境变量/配置文件 → 代码默认值」。
/// 实现必须是无阻塞的（读内存快照），因为它会在请求热路径上被反复调用。
/// </summary>
public interface ISettingsProvider
{
    /// <summary>返回生效值；机密设置返回解密后的明文。未配置时返回 null。</summary>
    string? GetValue(string key);

    SettingSource GetSource(string key);
}

public static class SettingsProviderExtensions
{
    public static int? GetInt(this ISettingsProvider provider, string key) =>
        int.TryParse(provider.GetValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static bool GetBool(this ISettingsProvider provider, string key, bool fallback) =>
        bool.TryParse(provider.GetValue(key), out var value) ? value : fallback;

    /// <summary>取 Choice 型设置，统一小写后比较，避免因为大小写不同而走错分支。</summary>
    public static string GetChoice(this ISettingsProvider provider, string key, string fallback) =>
        (provider.GetValue(key) ?? fallback).Trim().ToLowerInvariant();
}
