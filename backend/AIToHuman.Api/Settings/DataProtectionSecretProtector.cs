using System.Security.Cryptography;
using AIToHuman.Application.Settings;
using Microsoft.AspNetCore.DataProtection;

namespace AIToHuman.Api.Settings;

/// <summary>
/// 用 ASP.NET Core Data Protection 加密机密配置。
/// 密钥环来自部署环境（默认在用户配置文件目录，可用 <c>DataProtection__KeysPath</c> 指到持久卷），
/// 不存放在配置表里，所以拿到数据库快照也读不出明文。
/// 多实例部署必须让所有实例共享同一个密钥环，否则会出现“A 实例保存的密钥 B 实例解不开”。
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private const string Prefix = "dp1:";

    private readonly IDataProtector protector;
    private readonly ILogger<DataProtectionSecretProtector> logger;

    public DataProtectionSecretProtector(IDataProtectionProvider provider, ILogger<DataProtectionSecretProtector> logger)
    {
        protector = provider.CreateProtector("AIToHuman.Settings.v1");
        this.logger = logger;
    }

    public string Protect(string plaintext) => Prefix + protector.Protect(plaintext);

    public string? Unprotect(string stored)
    {
        // 兼容没有前缀的历史值：按明文处理，避免升级后旧配置直接失效。
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        try
        {
            return protector.Unprotect(stored[Prefix.Length..]);
        }
        catch (CryptographicException exception)
        {
            logger.LogError(exception, "机密配置解密失败：Data Protection 密钥环可能已更换。");
            return null;
        }
    }
}
