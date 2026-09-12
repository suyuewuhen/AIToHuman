using System.Globalization;
using AIToHuman.Domain.Common;

namespace AIToHuman.Application.Settings;

/// <summary>取值的类型，决定运营后台用哪种控件编辑、服务端做哪种校验。</summary>
public enum SettingValueKind
{
    String,
    Bool,
    Int,
    Url,
    Choice
}

/// <summary>
/// 一个已注册设置键的元数据。
/// 设置目录就是白名单：只有登记在册的键才能被运营后台读写，
/// 因此连接串、日志、密钥环路径这类部署级配置永远不会出现在配置表里。
/// </summary>
public sealed record SettingDefinition(
    string Key,
    string Category,
    string DisplayName,
    string Description,
    SettingValueKind Kind,
    string DefaultValue,
    string ConfigurationKey,
    bool IsSecret = false,
    IReadOnlyList<string>? Choices = null,
    int MinInt = int.MinValue,
    int MaxInt = int.MaxValue,
    int MaxLength = 200)
{
    public IReadOnlyList<string> AllowedValues => Choices ?? [];

    /// <summary>
    /// 校验并规范化运营后台提交的值。空字符串表示“显式置空”（例如停用某个集成），
    /// 非法取值抛 <see cref="DomainException"/>，由 API 层映射为 422。
    /// </summary>
    public string EnsureValue(string? value)
    {
        if (value is null) throw new DomainException($"设置 {Key} 的值不能为空。");

        var trimmed = value.Trim();
        switch (Kind)
        {
            case SettingValueKind.Bool:
                if (!bool.TryParse(trimmed, out var flag)) throw new DomainException($"设置 {Key} 只能是 true 或 false。");
                return flag ? "true" : "false";

            case SettingValueKind.Int:
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    throw new DomainException($"设置 {Key} 必须是整数。");
                if (number < MinInt || number > MaxInt)
                    throw new DomainException($"设置 {Key} 必须在 {MinInt} 到 {MaxInt} 之间。");
                return number.ToString(CultureInfo.InvariantCulture);

            case SettingValueKind.Url:
                if (trimmed.Length == 0) return string.Empty;
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new DomainException($"设置 {Key} 必须是 http 或 https 的绝对地址。");
                return trimmed.TrimEnd('/');

            case SettingValueKind.Choice:
                var matched = AllowedValues.FirstOrDefault(item => string.Equals(item, trimmed, StringComparison.OrdinalIgnoreCase));
                if (matched is null) throw new DomainException($"设置 {Key} 只能是 {string.Join("、", AllowedValues)} 之一。");
                return matched;

            default:
                if (trimmed.Length > MaxLength) throw new DomainException($"设置 {Key} 不能超过 {MaxLength} 个字符。");
                return trimmed;
        }
    }
}
