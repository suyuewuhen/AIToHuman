using AIToHuman.Contracts.Settings;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Settings;

namespace AIToHuman.Application.Settings;

/// <summary>
/// 运营配置用例：列表、写入覆盖、恢复默认、自检与审计查询。
/// 每次写入都要过两道闸门——设置目录（是不是注册过的键）与取值校验（类型、范围、枚举）；
/// 写入与审计在同一个事务里提交，成功后立即刷新内存快照，新值马上生效。
/// </summary>
public sealed class SettingsService(
    ISystemSettingsRepository repository,
    ISettingsProvider provider,
    ISecretProtector protector,
    ISettingsReloader reloader,
    ISettingProbe probe,
    TimeProvider timeProvider)
{
    /// <summary>一次最多返回多少条审计记录。</summary>
    public const int MaxAuditLimit = 200;

    private const string NotSetDisplay = "(未设置)";
    private const string ResetDisplay = "(恢复默认)";
    private const string BrokenSecretDisplay = "(无法解密，请重新保存)";

    public IReadOnlyCollection<AdminSettingResponse> List()
    {
        // 一次读完所有覆盖记录，避免为目录里的每个键各查一次库。
        var overrides = repository.List().ToDictionary(item => item.Key, StringComparer.Ordinal);
        return SettingCatalog.All.Select(definition => Describe(definition, overrides.GetValueOrDefault(definition.Key))).ToArray();
    }

    public AdminSettingResponse Get(string key)
    {
        var definition = SettingCatalog.Require(key);
        return Describe(definition, repository.Get(definition.Key));
    }

    public IReadOnlyCollection<SettingAuditResponse> Audits(int limit) =>
        repository.ListAudits(Math.Clamp(limit, 1, MaxAuditLimit))
            .Select(item => new SettingAuditResponse(item.Id, item.Key, item.Action.ToString(), item.OldValue, item.NewValue, item.ActorId, item.OccurredAt))
            .ToArray();

    public async Task<AdminSettingResponse> UpdateAsync(string key, UpdateSettingRequest request, Guid actorId, CancellationToken cancellationToken = default)
    {
        var definition = SettingCatalog.Require(key);
        var normalized = definition.EnsureValue(request.Value);
        var existing = repository.Get(definition.Key);
        EnsureVersionMatches(definition.Key, existing, request.ExpectedVersion);

        var now = timeProvider.GetUtcNow();
        var audit = SettingsAuditEntry.Record(
            definition.Key,
            SettingsAuditAction.Update,
            DescribeStoredValue(definition, existing),
            DescribeNewValue(definition, normalized),
            actorId,
            now);

        // 机密先加密再进领域与仓储：领域层和数据库都拿不到明文。
        var stored = definition.IsSecret ? protector.Protect(normalized) : normalized;
        var setting = existing is null
            ? SystemSetting.Create(definition.Key, stored, definition.IsSecret, actorId, now)
            : Update(existing, stored, actorId, now);

        repository.Save(setting, audit);
        await reloader.ReloadAsync(cancellationToken);
        return Describe(definition, repository.Get(definition.Key));
    }

    /// <summary>恢复默认：删除覆盖记录，配置立即回退到环境变量或代码默认值。</summary>
    public async Task<AdminSettingResponse> ResetAsync(string key, Guid actorId, CancellationToken cancellationToken = default)
    {
        var definition = SettingCatalog.Require(key);
        var existing = repository.Get(definition.Key);
        if (existing is not null)
        {
            var audit = SettingsAuditEntry.Record(
                definition.Key,
                SettingsAuditAction.Reset,
                DescribeStoredValue(definition, existing),
                ResetDisplay,
                actorId,
                timeProvider.GetUtcNow());

            repository.Remove(definition.Key, audit);
            await reloader.ReloadAsync(cancellationToken);
        }

        return Describe(definition, repository.Get(definition.Key));
    }

    public Task<SettingTestResponse> TestAsync(string key, CancellationToken cancellationToken = default) =>
        probe.ProbeAsync(SettingCatalog.Require(key).Key, cancellationToken);

    private static SystemSetting Update(SystemSetting existing, string stored, Guid actorId, DateTimeOffset now)
    {
        existing.SetValue(stored, actorId, now);
        return existing;
    }

    /// <summary>客户端带上版本号时先做一次比较，避免用旧值覆盖别人刚改的内容（409）。</summary>
    private static void EnsureVersionMatches(string key, SystemSetting? existing, int? expectedVersion)
    {
        if (expectedVersion is not { } expected) return;

        var actual = existing?.Version ?? 0;
        if (actual != expected)
            throw new ConcurrencyConflictException($"配置 {key} 已被其他人修改（当前版本 {actual}），请刷新后重试。");
    }

    private AdminSettingResponse Describe(SettingDefinition definition, SystemSetting? overrideRow)
    {
        var effective = provider.GetValue(definition.Key);

        // 有覆盖记录却读不出明文，说明加密密钥环换了：这时既不能显示旧值，也不能假装它是默认值。
        var display = definition.IsSecret
            ? overrideRow is not null && effective is null ? BrokenSecretDisplay : SettingMasking.Mask(effective)
            : effective ?? string.Empty;

        var source = overrideRow is not null ? SettingSource.Database : provider.GetSource(definition.Key);

        return new AdminSettingResponse(
            definition.Key,
            definition.Category,
            definition.DisplayName,
            definition.Description,
            definition.Kind.ToString(),
            definition.IsSecret,
            display,
            definition.DefaultValue,
            definition.ConfigurationKey,
            definition.AllowedValues,
            source.ToString().ToLowerInvariant(),
            overrideRow is not null,
            overrideRow?.Version,
            overrideRow?.UpdatedBy,
            overrideRow?.UpdatedAt,
            SettingMasking.Fingerprint(effective));
    }

    /// <summary>审计里的“旧值”：机密先解密再脱敏，永远不写明文。</summary>
    private string DescribeStoredValue(SettingDefinition definition, SystemSetting? existing)
    {
        if (existing is null) return NotSetDisplay;
        return definition.IsSecret ? SettingMasking.Mask(protector.Unprotect(existing.Value)) : existing.Value;
    }

    /// <summary>审计里的“新值”：机密写掩码加指纹，非机密直接写明文，便于追责。</summary>
    private static string DescribeNewValue(SettingDefinition definition, string normalized) =>
        definition.IsSecret
            ? $"{SettingMasking.Mask(normalized)}({SettingMasking.Fingerprint(normalized)})"
            : normalized;
}
