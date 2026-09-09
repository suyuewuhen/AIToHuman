namespace AIToHuman.Contracts;

/// <summary>本地开发联调用的合成会话，正式环境由 JWT/身份服务替换。</summary>
public sealed record DevSessionResponse(Guid UserId, string DisplayName, string Role, IReadOnlyList<string> AvailableRoles, bool IsDevelopmentSession);
