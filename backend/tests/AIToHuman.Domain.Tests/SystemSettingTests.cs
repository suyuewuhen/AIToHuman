using AIToHuman.Domain.Common;
using AIToHuman.Domain.Settings;

namespace AIToHuman.Domain.Tests;

public sealed class SystemSettingTests
{
    private static readonly Guid Actor = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherActor = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_setting_starts_at_version_one_and_trims_value()
    {
        var setting = SystemSetting.Create("ai.apiKey", "  sk-123  ", true, Actor, Now);

        Assert.Equal("ai.apiKey", setting.Key);
        Assert.Equal("sk-123", setting.Value);
        Assert.True(setting.IsSecret);
        Assert.Equal(1, setting.Version);
        Assert.Equal(Actor, setting.UpdatedBy);
        Assert.Equal(Now, setting.UpdatedAt);
    }

    [Fact]
    public void SetValue_bumps_version_and_records_new_actor()
    {
        var setting = SystemSetting.Create("ai.model", "model-a", false, Actor, Now);

        setting.SetValue("model-b", OtherActor, Now.AddHours(1));

        Assert.Equal(2, setting.Version);
        Assert.Equal("model-b", setting.Value);
        Assert.Equal(OtherActor, setting.UpdatedBy);
        Assert.Equal(Now.AddHours(1), setting.UpdatedAt);
    }

    [Theory]
    [InlineData("ConnectionStrings:Postgres")]
    [InlineData("ai..apiKey")]
    [InlineData("ai.apiKey!")]
    [InlineData("1ai.model")]
    [InlineData("ai.密钥")]
    [InlineData("")]
    public void Rejects_malformed_keys(string key) =>
        Assert.Throws<DomainException>(() => SystemSetting.Create(key, "value", false, Actor, Now));

    [Fact]
    public void Rejects_key_longer_than_limit() =>
        Assert.Throws<DomainException>(() => SystemSetting.Create(new string('a', SystemSetting.MaxKeyLength + 1), "value", false, Actor, Now));

    [Fact]
    public void Rejects_value_longer_than_limit() =>
        Assert.Throws<DomainException>(() => SystemSetting.Create("ai.model", new string('x', SystemSetting.MaxValueLength + 1), false, Actor, Now));

    [Fact]
    public void Rejects_empty_actor() =>
        Assert.Throws<DomainException>(() => SystemSetting.Create("ai.model", "model", false, Guid.Empty, Now));

    [Fact]
    public void Normalizes_timestamps_to_utc()
    {
        var offset = new DateTimeOffset(2026, 9, 12, 16, 0, 0, TimeSpan.FromHours(8));

        var setting = SystemSetting.Create("ai.model", "model", false, Actor, offset);

        Assert.Equal(TimeSpan.Zero, setting.UpdatedAt.Offset);
        Assert.Equal(offset.UtcDateTime, setting.UpdatedAt.UtcDateTime);
    }

    [Fact]
    public void Rehydrate_keeps_persisted_version() =>
        Assert.Equal(7, SystemSetting.Rehydrate("ai.model", "model", false, 7, Actor, Now).Version);

    [Fact]
    public void Rehydrate_rejects_non_positive_version() =>
        Assert.Throws<DomainException>(() => SystemSetting.Rehydrate("ai.model", "model", false, 0, Actor, Now));

    [Fact]
    public void Audit_marks_missing_old_value_and_truncates_long_value()
    {
        var entry = SettingsAuditEntry.Record("ai.model", SettingsAuditAction.Update, null, new string('x', 500), Actor, Now);

        Assert.Equal("(空)", entry.OldValue);
        Assert.Equal(SettingsAuditEntry.MaxValueLength + 1, entry.NewValue.Length);
        Assert.EndsWith("…", entry.NewValue, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_requires_actor() =>
        Assert.Throws<DomainException>(() => SettingsAuditEntry.Record("ai.model", SettingsAuditAction.Reset, "a", "b", Guid.Empty, Now));
}
