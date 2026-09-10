namespace AIToHuman.Domain.Common;

/// <summary>
/// 领域内的时刻统一以 UTC 保存。
/// 客户端、浏览器和 AI 都可能提交带本地偏移量的时间（例如 <c>+08:00</c>），
/// 而 PostgreSQL 的 <c>timestamp with time zone</c> 只接受零偏移量，
/// 直接写入会抛出 <see cref="ArgumentException"/> 并导致请求 500。
/// </summary>
public static class UtcTimestamp
{
    public static DateTimeOffset Normalize(DateTimeOffset value) => value.ToUniversalTime();
}
