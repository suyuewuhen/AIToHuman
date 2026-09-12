namespace AIToHuman.Contracts;

/// <summary>本地开发联调用的合成会话，正式环境由 JWT/身份服务替换。</summary>
public sealed record DevSessionResponse(Guid UserId, string DisplayName, string Role, IReadOnlyList<string> AvailableRoles, bool IsDevelopmentSession);
public sealed record RegisterRequest(string Email, string Password, string DisplayName, string Role);
public sealed record LoginRequest(string Email, string Password);
public sealed record AuthResponse(Guid UserId, string Email, string DisplayName, string Role, string AccessToken, int ExpiresInSeconds);
/// <summary>当前登录用户。<see cref="IsAdmin"/> 决定前端是否展示运营配置入口，真正的授权仍在服务端。</summary>
public sealed record CurrentUserResponse(Guid UserId, string Email, string DisplayName, string Role, bool IsAdmin);
public sealed record SwitchRoleRequest(string Role);
