using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Settings;
using AIToHuman.Domain.Common;

namespace AIToHuman.IntegrationTests;

public sealed class SettingsServiceTests
{
    private static readonly Guid Admin = SettingsTestData.Admin;

    [Fact]
    public void Catalog_is_visible_with_defaults_and_no_override()
    {
        var host = new TestSettingsHost();

        var items = host.Service.List();

        Assert.Equal(SettingCatalog.All.Count, items.Count);

        var model = host.Service.Get(SettingKeys.AiModel);
        Assert.Equal("glm-4-7-251222", model.Value);
        Assert.Equal("default", model.Source);
        Assert.False(model.HasOverride);
        Assert.Null(model.OverrideVersion);
    }

    [Fact]
    public void Configuration_beats_default()
    {
        var host = new TestSettingsHost(new Dictionary<string, string?> { ["VolcengineAI:Model"] = "cfg-model" });

        var model = host.Service.Get(SettingKeys.AiModel);

        Assert.Equal("cfg-model", model.Value);
        Assert.Equal("configuration", model.Source);
        Assert.Equal("configuration", host.Provider.GetSource(SettingKeys.AiModel).ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task Database_override_beats_configuration_and_takes_effect_immediately()
    {
        var host = new TestSettingsHost(new Dictionary<string, string?> { ["VolcengineAI:Model"] = "cfg-model" });

        var updated = await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("db-model"), Admin);

        Assert.Equal("db-model", updated.Value);
        Assert.Equal("database", updated.Source);
        Assert.True(updated.HasOverride);
        Assert.Equal(1, updated.OverrideVersion);
        Assert.Equal(Admin, updated.UpdatedBy);
        // 消费方读的是刷新后的快照，不需要重启进程。
        Assert.Equal("db-model", host.Provider.GetValue(SettingKeys.AiModel));
    }

    [Fact]
    public async Task Secret_is_encrypted_at_rest_and_never_returned_in_clear()
    {
        var host = new TestSettingsHost();
        const string plaintext = "sk-live-abcdef123456";

        var updated = await host.Service.UpdateAsync(SettingKeys.AiApiKey, new UpdateSettingRequest(plaintext), Admin);

        Assert.True(updated.IsSecret);
        Assert.Equal("****3456", updated.Value);
        Assert.DoesNotContain("abcdef", updated.Value, StringComparison.Ordinal);
        Assert.NotEmpty(updated.Fingerprint);

        // 落库的是密文，列表接口也不会出现明文。
        var stored = host.Repository.Get(SettingKeys.AiApiKey)!;
        Assert.StartsWith("fake:", stored.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef", stored.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(
            host.Service.List(),
            item => item.Value.Contains("abcdef", StringComparison.Ordinal));

        // 服务端内部仍然能拿到明文，供模型调用使用。
        Assert.Equal(plaintext, host.Provider.GetValue(SettingKeys.AiApiKey));
        Assert.Contains(plaintext, host.Protector.Protected);
    }

    [Fact]
    public async Task Audit_records_actor_and_masks_secrets()
    {
        var host = new TestSettingsHost();

        await host.Service.UpdateAsync(SettingKeys.AiApiKey, new UpdateSettingRequest("sk-first-1111"), Admin);
        await host.Service.UpdateAsync(SettingKeys.AiApiKey, new UpdateSettingRequest("sk-second-2222"), Admin);

        var audits = host.Audits();
        Assert.Equal(2, audits.Length);

        var latest = audits[0];
        Assert.Equal(SettingKeys.AiApiKey, latest.Key);
        Assert.Equal("Update", latest.Action);
        Assert.Equal(Admin, latest.ActorId);
        Assert.Contains("****2222", latest.NewValue, StringComparison.Ordinal);
        Assert.Contains("****1111", latest.OldValue, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-second", latest.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_records_plaintext_for_non_secret_keys()
    {
        var host = new TestSettingsHost();

        await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("model-x"), Admin);

        var latest = host.Audits()[0];
        Assert.Equal("(未设置)", latest.OldValue);
        Assert.Equal("model-x", latest.NewValue);
    }

    [Fact]
    public async Task Stale_version_is_rejected_with_conflict()
    {
        var host = new TestSettingsHost();
        await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("model-a"), Admin);

        // 用过期版本（0）提交，模拟两个管理员同时编辑。
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("model-b", ExpectedVersion: 0), Admin));

        // 带上正确版本可以继续修改。
        var updated = await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("model-b", ExpectedVersion: 1), Admin);
        Assert.Equal("model-b", updated.Value);
        Assert.Equal(2, updated.OverrideVersion);
    }

    [Fact]
    public async Task Insert_with_expected_version_zero_is_allowed_once()
    {
        var host = new TestSettingsHost();

        await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("model-a", ExpectedVersion: 0), Admin);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("model-b", ExpectedVersion: 0), Admin));
    }

    [Fact]
    public async Task Reset_removes_override_and_restores_fallback()
    {
        var host = new TestSettingsHost(new Dictionary<string, string?> { ["VolcengineAI:Model"] = "cfg-model" });
        await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("db-model"), Admin);

        // 审计按发生时间倒序，真实时钟会推进；这里手动前进来让“恢复默认”成为最新一条。
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        var reset = await host.Service.ResetAsync(SettingKeys.AiModel, Admin);

        Assert.Equal("cfg-model", reset.Value);
        Assert.Equal("configuration", reset.Source);
        Assert.False(reset.HasOverride);
        Assert.Null(host.Repository.Get(SettingKeys.AiModel));

        var latest = host.Audits()[0];
        Assert.Equal("Reset", latest.Action);
        Assert.Equal("db-model", latest.OldValue);
        Assert.Equal("(恢复默认)", latest.NewValue);
    }

    [Fact]
    public async Task Reset_without_override_is_a_no_op_and_writes_no_audit()
    {
        var host = new TestSettingsHost();

        var reset = await host.Service.ResetAsync(SettingKeys.AiModel, Admin);

        Assert.Equal("default", reset.Source);
        Assert.Empty(host.Audits());
    }

    [Fact]
    public async Task Unregistered_key_is_rejected_as_bad_request()
    {
        var host = new TestSettingsHost();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            host.Service.UpdateAsync("ConnectionStrings.Postgres", new UpdateSettingRequest("Host=evil"), Admin));

        Assert.Contains("未注册的设置键", exception.Message, StringComparison.Ordinal);
        Assert.Empty(host.Repository.List());
    }

    [Theory]
    [InlineData(SettingKeys.AiInactivityTimeoutSeconds, "1")]
    [InlineData(SettingKeys.AiInactivityTimeoutSeconds, "abc")]
    [InlineData(SettingKeys.StorageProvider, "ftp")]
    [InlineData(SettingKeys.EvidenceScannerEndpoint, "scanner.example.com")]
    public async Task Invalid_values_are_rejected(string key, string value)
    {
        var host = new TestSettingsHost();

        await Assert.ThrowsAsync<DomainException>(() => host.Service.UpdateAsync(key, new UpdateSettingRequest(value), Admin));

        Assert.Empty(host.Repository.List());
        Assert.Empty(host.Audits());
    }

    [Fact]
    public async Task Broken_key_ring_is_reported_instead_of_silently_showing_default()
    {
        var host = new TestSettingsHost();
        await host.Service.UpdateAsync(SettingKeys.AiApiKey, new UpdateSettingRequest("sk-live-abcdef123456"), Admin);

        // 模拟密钥环更换：解密失败后快照里不再有该键的明文。
        host.Protector.Broken = true;
        host.Reload();

        var item = host.Service.Get(SettingKeys.AiApiKey);
        Assert.Equal("(无法解密，请重新保存)", item.Value);
        Assert.Equal("database", item.Source);
        Assert.True(item.HasOverride);
        Assert.Null(host.Provider.GetValue(SettingKeys.AiApiKey));
    }

    [Fact]
    public async Task Audit_limit_is_clamped_to_a_useful_range()
    {
        var host = new TestSettingsHost();
        await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("m1"), Admin);
        await host.Service.UpdateAsync(SettingKeys.AiModel, new UpdateSettingRequest("m2"), Admin);

        Assert.Single(host.Service.Audits(0));
        Assert.Equal(2, host.Service.Audits(500).Count);
    }

    [Fact]
    public async Task Test_uses_the_probe_for_the_registered_key()
    {
        var probe = new StubProbe(new SettingTestResponse(string.Empty, true, "目录可用"));
        var host = new TestSettingsHost(probe: probe);

        var result = await host.Service.TestAsync(SettingKeys.StorageLocalRoot);

        Assert.True(result.Ok);
        Assert.Equal(SettingKeys.StorageLocalRoot, result.Key);
        Assert.Equal("目录可用", result.Message);
        Assert.Equal(new[] { SettingKeys.StorageLocalRoot }, probe.Keys);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Service.TestAsync("unknown.key"));
    }

    [Fact]
    public async Task Snapshot_ignores_rows_that_are_not_in_the_catalog()
    {
        var host = new TestSettingsHost();
        // 直接往仓储里塞一条未注册的键，模拟历史遗留或被手工写入的数据。
        host.Repository.Save(
            AIToHuman.Domain.Settings.SystemSetting.Create("legacy.setting", "value", false, Admin, host.Clock.GetUtcNow()),
            AIToHuman.Domain.Settings.SettingsAuditEntry.Record("legacy.setting", AIToHuman.Domain.Settings.SettingsAuditAction.Update, null, "value", Admin, host.Clock.GetUtcNow()));

        host.Reload();

        Assert.Null(host.Provider.GetValue("legacy.setting"));
        Assert.Equal(SettingCatalog.All.Count, host.Service.List().Count);
    }
}
