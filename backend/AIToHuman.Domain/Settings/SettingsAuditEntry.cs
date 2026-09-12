using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Settings;

public enum SettingsAuditAction
{
    Update,
    Reset
}

/// <summary>
/// 配置变更审计：只追加，不更新也不删除。
/// <see cref="OldValue"/> 与 <see cref="NewValue"/> 由应用层写入前脱敏，
/// 机密只留下掩码与指纹，审计表里永远不出现明文。
/// </summary>
public sealed class SettingsAuditEntry
{
    /// <summary>审计里的取值长度上限，避免超长值把审计表撑大。</summary>
    public const int MaxValueLength = 200;

    private SettingsAuditEntry(Guid id, string key, SettingsAuditAction action, string oldValue, string newValue, Guid actorId, DateTimeOffset occurredAt)
    {
        Id = id;
        Key = key;
        Action = action;
        OldValue = oldValue;
        NewValue = newValue;
        ActorId = actorId;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }
    public string Key { get; }
    public SettingsAuditAction Action { get; }
    public string OldValue { get; }
    public string NewValue { get; }
    public Guid ActorId { get; }
    public DateTimeOffset OccurredAt { get; }

    public static SettingsAuditEntry Record(string key, SettingsAuditAction action, string? oldValue, string? newValue, Guid actorId, DateTimeOffset occurredAt)
    {
        if (actorId == Guid.Empty) throw new DomainException("配置变更必须记录操作人。");

        return new SettingsAuditEntry(
            Guid.NewGuid(),
            SystemSetting.EnsureKey(key),
            action,
            Describe(oldValue),
            Describe(newValue),
            actorId,
            UtcTimestamp.Normalize(occurredAt));
    }

    public static SettingsAuditEntry Rehydrate(Guid id, string key, SettingsAuditAction action, string oldValue, string newValue, Guid actorId, DateTimeOffset occurredAt) =>
        new(id, SystemSetting.EnsureKey(key), action, Describe(oldValue), Describe(newValue), actorId, UtcTimestamp.Normalize(occurredAt));

    private static string Describe(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "(空)" : value.Trim();
        return text.Length <= MaxValueLength ? text : text[..MaxValueLength] + "…";
    }
}
