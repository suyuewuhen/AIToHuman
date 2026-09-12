using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Settings;

/// <summary>
/// 一条运营可配置项：表示对某个已注册设置键的显式覆盖。
/// 没有记录时配置解析会依次回退到环境变量/配置文件与代码默认值，
/// 所以“删除记录”就等于恢复默认，不需要额外的状态列。
/// 机密设置（API Key、访问密钥）在这里存的是 <c>ISecretProtector</c> 产生的密文，
/// 领域层与仓储都不接触明文。
/// </summary>
public sealed class SystemSetting
{
    /// <summary>设置键长度上限。</summary>
    public const int MaxKeyLength = 120;

    /// <summary>单条取值的落库上限。机密在这里是密文（Base64 后约为明文的 1.4 倍），因此留出余量。</summary>
    public const int MaxValueLength = 8000;

    private SystemSetting(string key, string value, bool isSecret, int version, Guid updatedBy, DateTimeOffset updatedAt)
    {
        Key = key;
        Value = value;
        IsSecret = isSecret;
        Version = version;
        UpdatedBy = updatedBy;
        UpdatedAt = updatedAt;
    }

    public string Key { get; }

    /// <summary>落库的取值：非机密是明文，机密是密文。</summary>
    public string Value { get; private set; }

    /// <summary>机密标记由设置目录决定，写入后不可更改，避免把密文当成明文展示。</summary>
    public bool IsSecret { get; }

    /// <summary>乐观并发令牌：每次写入自增，仓储用它生成 WHERE 条件。</summary>
    public int Version { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SystemSetting Create(string key, string value, bool isSecret, Guid updatedBy, DateTimeOffset updatedAt) =>
        new(EnsureKey(key), EnsureValue(value), isSecret, 1, EnsureActor(updatedBy), UtcTimestamp.Normalize(updatedAt));

    /// <summary>由仓储按已落库的版本号还原聚合，供写入时做乐观并发比较。</summary>
    public static SystemSetting Rehydrate(string key, string value, bool isSecret, int version, Guid updatedBy, DateTimeOffset updatedAt)
    {
        if (version < 1) throw new DomainException("设置版本号必须大于零。");
        return new(EnsureKey(key), EnsureValue(value), isSecret, version, EnsureActor(updatedBy), UtcTimestamp.Normalize(updatedAt));
    }

    /// <summary>写入新的覆盖值：版本号自增，仓储据此判断是否有人抢先改过。</summary>
    public void SetValue(string value, Guid updatedBy, DateTimeOffset updatedAt)
    {
        Value = EnsureValue(value);
        UpdatedBy = EnsureActor(updatedBy);
        UpdatedAt = UtcTimestamp.Normalize(updatedAt);
        Version++;
    }

    /// <summary>
    /// 设置键的形状：点号分段，每一段以字母开头、只含字母和数字，例如 <c>ai.apiKey</c>、<c>storage.s3.bucket</c>。
    /// 真正的白名单在应用层的设置目录里，这里只挡掉明显写错或想塞连接串的写法。
    /// </summary>
    public static string EnsureKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new DomainException("设置键不能为空。");

        var trimmed = key.Trim();
        if (trimmed.Length > MaxKeyLength) throw new DomainException($"设置键不能超过 {MaxKeyLength} 个字符。");

        foreach (var segment in trimmed.Split('.'))
        {
            if (segment.Length == 0) throw new DomainException("设置键不能包含空的点分段，例如 ai..apiKey。");
            if (!char.IsAsciiLetter(segment[0])) throw new DomainException("设置键的每一段都必须以字母开头，例如 ai.apiKey。");
            if (!segment.All(char.IsAsciiLetterOrDigit)) throw new DomainException("设置键只允许字母、数字和点号，例如 storage.s3.bucket。");
        }

        return trimmed;
    }

    /// <summary>取值首尾空白一律去掉：粘贴出来的密钥带空格是常见事故。</summary>
    public static string EnsureValue(string value)
    {
        if (value is null) throw new DomainException("设置值不能为空对象。");

        var trimmed = value.Trim();
        if (trimmed.Length > MaxValueLength) throw new DomainException($"设置值不能超过 {MaxValueLength} 个字符。");
        return trimmed;
    }

    private static Guid EnsureActor(Guid updatedBy) =>
        updatedBy == Guid.Empty ? throw new DomainException("配置变更必须记录操作人。") : updatedBy;
}
