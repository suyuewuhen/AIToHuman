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

    /// <summary>
    /// 同时支持 <c>Admin__UserIds=a,b</c>（标量）与数组形式的 <c>Admin__UserIds__0=a</c>。
    /// 标量优先：环境变量比 appsettings 优先级更高，但它在配置系统里只是同一个键的值，
    /// 如果先看数组子项，appsettings 里的数组会把环境变量的覆盖悄悄吃掉。
    /// </summary>
    private static IEnumerable<string> ReadValues(IConfiguration configuration, string key)
    {
        var single = configuration[key];
        if (!string.IsNullOrWhiteSpace(single))
            return single.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return configuration.GetSection(key)
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }
}
