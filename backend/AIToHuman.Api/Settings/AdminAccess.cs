using System.Security.Claims;

namespace AIToHuman.Api.Settings;

/// <summary>
/// 运营后台的身份判定。管理员名单来自部署配置（<c>Admin__UserIds</c> 或 <c>Admin__Emails</c>），
/// 刻意不放进可被后台修改的配置表里：否则任何能改配置的人都能把自己提成管理员（权限自举）。
/// </summary>
public sealed class AdminAccess
{
    public const string PolicyName = "admin";

    private readonly HashSet<Guid> userIds;
    private readonly HashSet<string> emails;

    public AdminAccess(IConfiguration configuration)
    {
        userIds = ReadValues(configuration, "Admin:UserIds")
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        emails = ReadValues(configuration, "Admin:Emails")
            .Select(value => value.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>没有配置任何管理员时，运营接口对所有人返回 403。</summary>
    public bool IsEmpty => userIds.Count == 0 && emails.Count == 0;

    /// <summary>已登录且命中名单（或带 <c>admin</c> 角色声明）才算管理员。</summary>
    public bool IsAdmin(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        if (user.IsInRole("admin")) return true;

        if (Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) && userIds.Contains(userId)) return true;

        var email = user.FindFirstValue(ClaimTypes.Email);
        return email is not null && emails.Contains(email.Trim().ToLowerInvariant());
    }

    /// <summary>同时支持 <c>Admin__UserIds=a,b</c> 与数组形式的 <c>Admin__UserIds__0=a</c>。</summary>
    private static IEnumerable<string> ReadValues(IConfiguration configuration, string key)
    {
        var section = configuration.GetSection(key);
        var children = section.GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        if (children.Length > 0) return children;

        var single = configuration[key];
        return string.IsNullOrWhiteSpace(single)
            ? []
            : single.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
