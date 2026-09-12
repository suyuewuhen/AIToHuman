using AIToHuman.Application.Settings;
using AIToHuman.Domain.Common;

namespace AIToHuman.IntegrationTests;

public sealed class SettingsCatalogTests
{
    [Fact]
    public void Keys_and_configuration_paths_are_unique()
    {
        Assert.Equal(SettingCatalog.All.Count, SettingCatalog.All.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(SettingCatalog.All.Count, SettingCatalog.All.Select(item => item.ConfigurationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_default_value_passes_its_own_validation() =>
        Assert.All(SettingCatalog.All, definition => Assert.Equal(definition.DefaultValue, definition.EnsureValue(definition.DefaultValue)));

    [Fact]
    public void Choice_definitions_list_their_allowed_values() =>
        Assert.All(
            SettingCatalog.All.Where(item => item.Kind == SettingValueKind.Choice),
            definition => Assert.NotEmpty(definition.AllowedValues));

    [Fact]
    public void Int_definitions_have_a_sane_range() =>
        Assert.All(
            SettingCatalog.All.Where(item => item.Kind == SettingValueKind.Int),
            definition => Assert.True(definition.MinInt < definition.MaxInt));

    [Fact]
    public void Require_rejects_unregistered_keys()
    {
        var exception = Assert.Throws<ArgumentException>(() => SettingCatalog.Require("ConnectionStrings.Postgres"));

        Assert.Contains("未注册的设置键", exception.Message, StringComparison.Ordinal);
        Assert.Null(SettingCatalog.TryGet("ConnectionStrings.Postgres"));
        Assert.NotNull(SettingCatalog.TryGet(SettingKeys.AiModel));
    }

    [Fact]
    public void Choice_validation_is_case_insensitive_and_normalizes()
    {
        var definition = SettingCatalog.Require(SettingKeys.StorageProvider);

        Assert.Equal("local", definition.EnsureValue("LOCAL"));
        Assert.Throws<DomainException>(() => definition.EnsureValue("ftp"));
    }

    [Fact]
    public void Url_validation_accepts_empty_and_rejects_relative()
    {
        var definition = SettingCatalog.Require(SettingKeys.EvidenceScannerEndpoint);

        Assert.Equal(string.Empty, definition.EnsureValue("  "));
        Assert.Equal("https://scanner.example.com/api", definition.EnsureValue("https://scanner.example.com/api/"));
        Assert.Throws<DomainException>(() => definition.EnsureValue("scanner.example.com"));
        Assert.Throws<DomainException>(() => definition.EnsureValue("ftp://scanner.example.com"));
    }

    [Fact]
    public void Int_validation_enforces_range()
    {
        var definition = SettingCatalog.Require(SettingKeys.AiInactivityTimeoutSeconds);

        Assert.Equal("120", definition.EnsureValue(" 120 "));
        Assert.Throws<DomainException>(() => definition.EnsureValue("1"));
        Assert.Throws<DomainException>(() => definition.EnsureValue("abc"));
    }

    [Fact]
    public void Secret_definitions_have_bounded_length() =>
        Assert.All(
            SettingCatalog.All.Where(item => item.IsSecret),
            definition => Assert.True(definition.MaxLength > 0 && definition.MaxLength <= 1000, $"{definition.Key} 的机密长度上限应被限制"));
}
