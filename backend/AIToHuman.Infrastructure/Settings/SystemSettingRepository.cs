using AIToHuman.Application.Settings;
using AIToHuman.Domain.Settings;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Settings;

/// <summary>
/// PostgreSQL 实现。覆盖值与审计记录在同一个 <c>SaveChanges</c>（同一事务）里落库，
/// 不会出现“值改了但审计没写”的中间态。
/// </summary>
public sealed class EfSystemSettingRepository(TaskDbContext db) : ISystemSettingsRepository
{
    public IReadOnlyCollection<SystemSetting> List() =>
        db.SystemSettings.AsNoTracking().OrderBy(item => item.Key).ToArray().Select(Map).ToArray();

    public SystemSetting? Get(string key) =>
        db.SystemSettings.AsNoTracking().FirstOrDefault(item => item.Key == key) is { } record ? Map(record) : null;

    public void Save(SystemSetting setting, SettingsAuditEntry audit)
    {
        var record = db.SystemSettings.FirstOrDefault(item => item.Key == setting.Key);
        if (record is null)
        {
            db.SystemSettings.Add(new SystemSettingRecord
            {
                Key = setting.Key,
                Value = setting.Value,
                IsSecret = setting.IsSecret,
                Version = setting.Version,
                UpdatedBy = setting.UpdatedBy,
                UpdatedAt = setting.UpdatedAt
            });
        }
        else
        {
            // record 的原始版本号来自数据库，EF 用它生成 WHERE；两个管理员同时保存时后写入者会抛出并发异常。
            record.Value = setting.Value;
            record.IsSecret = setting.IsSecret;
            record.Version = setting.Version;
            record.UpdatedBy = setting.UpdatedBy;
            record.UpdatedAt = setting.UpdatedAt;
        }

        db.SettingsAudits.Add(MapAudit(audit));
        db.SaveChanges();
    }

    public void Remove(string key, SettingsAuditEntry audit)
    {
        if (db.SystemSettings.FirstOrDefault(item => item.Key == key) is { } record) db.SystemSettings.Remove(record);
        db.SettingsAudits.Add(MapAudit(audit));
        db.SaveChanges();
    }

    public IReadOnlyCollection<SettingsAuditEntry> ListAudits(int limit) =>
        db.SettingsAudits
            .AsNoTracking()
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.Id)
            .Take(limit)
            .ToArray()
            .Select(Map)
            .ToArray();

    private static SystemSetting Map(SystemSettingRecord record) =>
        SystemSetting.Rehydrate(record.Key, record.Value, record.IsSecret, record.Version, record.UpdatedBy, record.UpdatedAt);

    private static SettingsAuditEntry Map(SettingsAuditRecord record) =>
        SettingsAuditEntry.Rehydrate(
            record.Id,
            record.Key,
            Enum.TryParse<SettingsAuditAction>(record.Action, out var action) ? action : SettingsAuditAction.Update,
            record.OldValue,
            record.NewValue,
            record.ActorId,
            record.OccurredAt);

    private static SettingsAuditRecord MapAudit(SettingsAuditEntry audit) => new()
    {
        Id = audit.Id,
        Key = audit.Key,
        Action = audit.Action.ToString(),
        OldValue = audit.OldValue,
        NewValue = audit.NewValue,
        ActorId = audit.ActorId,
        OccurredAt = audit.OccurredAt
    };
}

/// <summary>
/// 内存实现：没有配置 PostgreSQL 时的回退，也用于测试。
/// 注意它把聚合实例直接存起来，因此只适合单进程开发场景。
/// </summary>
public sealed class InMemorySystemSettingRepository : ISystemSettingsRepository
{
    private readonly Dictionary<string, SystemSetting> settings = new(StringComparer.Ordinal);
    private readonly List<SettingsAuditEntry> audits = [];
    private readonly Lock gate = new();

    public IReadOnlyCollection<SystemSetting> List()
    {
        lock (gate) return settings.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
    }

    public SystemSetting? Get(string key)
    {
        lock (gate) return settings.GetValueOrDefault(key);
    }

    public void Save(SystemSetting setting, SettingsAuditEntry audit)
    {
        lock (gate)
        {
            settings[setting.Key] = setting;
            audits.Add(audit);
        }
    }

    public void Remove(string key, SettingsAuditEntry audit)
    {
        lock (gate)
        {
            settings.Remove(key);
            audits.Add(audit);
        }
    }

    public IReadOnlyCollection<SettingsAuditEntry> ListAudits(int limit)
    {
        lock (gate)
        {
            return audits.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id).Take(limit).ToArray();
        }
    }
}
