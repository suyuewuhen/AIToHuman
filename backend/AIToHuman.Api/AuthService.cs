using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AIToHuman.Contracts;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.IdentityModel.Tokens;

public sealed class AuthService(TaskDbContext db, IConfiguration configuration, TimeProvider timeProvider)
{
    private static readonly string[] Roles = ["owner", "worker"];

    public AuthResponse Register(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);
        ValidatePassword(request.Password);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 80) throw new InvalidOperationException("昵称必须为 1 到 80 个字符。");
        if (!Roles.Contains(request.Role, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("角色必须是 owner 或 worker。");
        if (db.Users.Any(item => item.Email == email)) throw new InvalidOperationException("该邮箱已注册。");
        var user = new UserRecord { Id = Guid.NewGuid(), Email = email, DisplayName = request.DisplayName.Trim(), Role = request.Role.ToLowerInvariant(), PasswordHash = HashPassword(request.Password), CreatedAt = timeProvider.GetUtcNow() };
        db.Users.Add(user);
        db.SaveChanges();
        return CreateResponse(user);
    }

    public AuthResponse Login(LoginRequest request)
    {
        var user = db.Users.SingleOrDefault(item => item.Email == NormalizeEmail(request.Email)) ?? throw new UnauthorizedAccessException("邮箱或密码不正确。");
        if (!VerifyPassword(request.Password, user.PasswordHash)) throw new UnauthorizedAccessException("邮箱或密码不正确。");
        return CreateResponse(user);
    }

    private AuthResponse CreateResponse(UserRecord user)
    {
        var expires = configuration.GetValue("Authentication:AccessTokenMinutes", 60);
        var key = new SymmetricSecurityKey(Convert.FromBase64String(configuration["Authentication:SigningKey"] ?? "QUlUb0h1bWFuLWxvY2FsLWRldi1rZXktMzItYnl0ZXMhIQ=="));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[] { new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()), new Claim(ClaimTypes.Email, user.Email), new Claim(ClaimTypes.Name, user.DisplayName), new Claim(ClaimTypes.Role, user.Role) };
        var token = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(expires), signingCredentials: credentials);
        return new(user.Id, user.Email, user.DisplayName, user.Role, new JwtSecurityTokenHandler().WriteToken(token), expires * 60);
    }

    private static string NormalizeEmail(string email) => string.IsNullOrWhiteSpace(email) ? throw new InvalidOperationException("邮箱不能为空。") : email.Trim().ToLowerInvariant();
    private static void ValidatePassword(string password) { if (string.IsNullOrWhiteSpace(password) || password.Length < 8) throw new InvalidOperationException("密码至少需要 8 个字符。"); }
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32);
        return $"v1:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }
    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]), 120_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[2]));
    }
}
