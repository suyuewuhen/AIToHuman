using System.Security.Claims;
using AIToHuman.Api.Settings;
using Microsoft.Extensions.Configuration;

namespace AIToHuman.IntegrationTests;

public sealed class AdminAccessTests
{
    private static readonly Guid AdminId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Configured_user_id_is_admin()
    {
        var access = Build(new Dictionary<string, string?> { ["Admin:UserIds"] = AdminId.ToString() });

        Assert.False(access.IsEmpty);
        Assert.True(access.IsAdmin(Principal(new Claim(ClaimTypes.NameIdentifier, AdminId.ToString()))));
        Assert.False(access.IsAdmin(Principal(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()))));
    }

    [Fact]
    public void Separated_and_array_configurations_both_work()
    {
        var other = Guid.NewGuid();
        var separated = Build(new Dictionary<string, string?> { ["Admin:UserIds"] = $"{AdminId}; {other}" });
        var array = Build(new Dictionary<string, string?>
        {
            ["Admin:UserIds:0"] = AdminId.ToString(),
            ["Admin:UserIds:1"] = other.ToString()
        });

        Assert.True(separated.IsAdmin(Principal(new Claim(ClaimTypes.NameIdentifier, other.ToString()))));
        Assert.True(array.IsAdmin(Principal(new Claim(ClaimTypes.NameIdentifier, other.ToString()))));
    }

    [Fact]
    public void Email_match_ignores_case()
    {
        var access = Build(new Dictionary<string, string?> { ["Admin:Emails"] = "Ops@Example.com" });

        Assert.True(access.IsAdmin(Principal(new Claim(ClaimTypes.Email, "ops@example.com"))));
        Assert.False(access.IsAdmin(Principal(new Claim(ClaimTypes.Email, "someone@example.com"))));
    }

    [Fact]
    public void Admin_role_claim_is_accepted_for_future_account_systems()
    {
        var access = Build([]);

        Assert.True(access.IsAdmin(Principal(new Claim(ClaimTypes.Role, "admin"))));
    }

    [Fact]
    public void Scalar_configuration_wins_over_an_array_from_lower_priority_source()
    {
        // 部署里常见的情形：appsettings 里是数组，环境变量是标量覆盖。
        // 标量的优先级更高，绝不能被数组子项盖掉（否则“配了管理员却还是 403”）。
        var access = Build(new Dictionary<string, string?>
        {
            ["Admin:Emails:0"] = "demo@aitohuman.local",
            ["Admin:Emails"] = "admin@example.com"
        });

        Assert.True(access.IsAdmin(Principal(new Claim(ClaimTypes.Email, "admin@example.com"))));
        Assert.False(access.IsAdmin(Principal(new Claim(ClaimTypes.Email, "demo@aitohuman.local"))));
    }

    [Fact]
    public void Anonymous_and_empty_configuration_are_denied()
    {
        var access = Build([]);

        Assert.True(access.IsEmpty);
        Assert.False(access.IsAdmin(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(access.IsAdmin(Principal(new Claim(ClaimTypes.NameIdentifier, AdminId.ToString()))));
    }

    private static AdminAccess Build(Dictionary<string, string?> configuration) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build());

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, authenticationType: "test"));
}
