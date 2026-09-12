namespace AIToHuman.Contracts.Settings;

/// <summary>
/// 运营后台看到的单条配置：包含生效值、来源与默认值。
/// 机密（<see cref="IsSecret"/>）的 <see cref="Value"/> 永远是掩码，明文只在服务端内部使用。
/// </summary>
public sealed record AdminSettingResponse(
    string Key,
    string Category,
    string DisplayName,
    string Description,
    string Kind,
    bool IsSecret,
    string Value,
    string DefaultValue,
    string ConfigurationKey,
    IReadOnlyList<string> AllowedValues,
    string Source,
    bool HasOverride,
    int? OverrideVersion,
    Guid? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    string Fingerprint);

/// <summary>
/// 写入覆盖值。<paramref name="ExpectedVersion"/> 为 null 表示不做版本检查；
/// 新增覆盖时传 0，已有覆盖时传上一步读到的 <c>overrideVersion</c>。
/// </summary>
public sealed record UpdateSettingRequest(string Value, int? ExpectedVersion = null);

/// <summary>“测试连接”的结果，只做只读自检。</summary>
public sealed record SettingTestResponse(string Key, bool Ok, string Message);

public sealed record SettingAuditResponse(
    Guid Id,
    string Key,
    string Action,
    string OldValue,
    string NewValue,
    Guid ActorId,
    DateTimeOffset OccurredAt);
