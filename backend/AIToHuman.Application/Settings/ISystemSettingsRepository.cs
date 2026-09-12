using AIToHuman.Domain.Settings;

namespace AIToHuman.Application.Settings;

/// <summary>
/// 配置覆盖值的持久化。
/// 写入必须和审计记录在同一个事务里完成，否则会出现“值改了但没人知道是谁改的”。
/// </summary>
public interface ISystemSettingsRepository
{
    IReadOnlyCollection<SystemSetting> List();

    SystemSetting? Get(string key);

    /// <summary>新增或更新覆盖值，并追加一条审计记录。</summary>
    void Save(SystemSetting setting, SettingsAuditEntry audit);

    /// <summary>删除覆盖值（恢复默认），并追加一条审计记录。</summary>
    void Remove(string key, SettingsAuditEntry audit);

    /// <summary>按时间倒序返回最近的审计记录。</summary>
    IReadOnlyCollection<SettingsAuditEntry> ListAudits(int limit);
}

/// <summary>
/// 机密设置落库前的加解密。
/// 加密密钥来自部署环境（Data Protection 密钥环），不存放在配置表里，
/// 因此拿到数据库快照也读不出明文。
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>解密失败（密钥环更换或数据被篡改）时返回 null，由调用方回退到环境变量并记录错误。</summary>
    string? Unprotect(string stored);
}

/// <summary>配置写入成功后刷新内存快照，让新值立即生效，不必重启进程。</summary>
public interface ISettingsReloader
{
    Task ReloadAsync(CancellationToken cancellationToken = default);
}
