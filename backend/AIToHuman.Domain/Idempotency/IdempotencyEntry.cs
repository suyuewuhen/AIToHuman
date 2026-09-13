using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Idempotency;

/// <summary>
/// 一次写请求的幂等记录：客户端带 <c>Idempotency-Key</c> 重试时，服务端凭它判断
/// "这个请求已经处理过"并原样回放上次的响应，而不是把加价、报名、选人这类动作再做一遍。
///
/// 生命周期：<see cref="Start"/> 占位（此时还没有响应）→ 执行业务 → <see cref="Complete"/> 记下状态码与响应体。
/// 占位与完成分开，是为了让"两个请求同时用同一个键"也被挡住：第二个请求会撞唯一索引。
/// </summary>
public sealed class IdempotencyEntry
{
    public const int MaxKeyLength = 120;
    public const int MaxRequestHashLength = 64;

    /// <summary>响应体的缓存上限：超了就干脆不缓存（宁可让重试重新执行，也不能回放半截 JSON）。</summary>
    public const int MaxResponseBodyLength = 32000;

    /// <summary>占位后超过这个时长还没写完，视为上次执行中途挂了，允许重试重新执行。</summary>
    public static readonly TimeSpan InFlightTimeout = TimeSpan.FromMinutes(2);

    private IdempotencyEntry()
    {
        Key = string.Empty;
        RequestHash = string.Empty;
    }

    public Guid UserId { get; private set; }
    public string Key { get; private set; }

    /// <summary>请求指纹（方法 + 路径 + 请求体）。同一个键配不同请求体说明客户端用错了键。</summary>
    public string RequestHash { get; private set; }

    public int? StatusCode { get; private set; }
    public string? ResponseBody { get; private set; }
    public string? ContentType { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    public static IdempotencyEntry Start(Guid userId, string key, string requestHash, DateTimeOffset now)
    {
        if (userId == Guid.Empty) throw new DomainException("幂等记录必须属于某个用户。");

        var normalizedKey = key?.Trim() ?? string.Empty;
        if (normalizedKey.Length is < 1 or > MaxKeyLength) throw new DomainException($"幂等键长度必须在 1 到 {MaxKeyLength} 个字符之间。");
        if (string.IsNullOrWhiteSpace(requestHash) || requestHash.Length > MaxRequestHashLength) throw new DomainException("幂等记录必须带上请求指纹。");

        return new IdempotencyEntry
        {
            UserId = userId,
            Key = normalizedKey,
            RequestHash = requestHash,
            StartedAt = UtcTimestamp.Normalize(now)
        };
    }

    /// <summary>记录响应。响应体超长时只记状态码，调用方据此删掉记录让重试重新执行。</summary>
    public bool Complete(int statusCode, string? responseBody, string? contentType, DateTimeOffset now)
    {
        if (statusCode is < 100 or > 599) throw new DomainException("幂等记录必须带上合法的 HTTP 状态码。");

        var body = responseBody ?? string.Empty;
        var storable = body.Length <= MaxResponseBodyLength;

        StatusCode = statusCode;
        ContentType = contentType;
        ResponseBody = storable ? body : null;
        CompletedAt = UtcTimestamp.Normalize(now);
        return storable;
    }

    /// <summary>同一个键不能用来提交不同的请求：那多半是客户端复用键的错误，必须报出来而不是回放旧响应。</summary>
    public bool Matches(string requestHash) => string.Equals(RequestHash, requestHash, StringComparison.Ordinal);

    /// <summary>占位很久还没完成：上次执行可能中途挂了，允许重试重新执行。</summary>
    public bool IsStale(DateTimeOffset now) => !IsCompleted && UtcTimestamp.Normalize(now) - StartedAt > InFlightTimeout;

    public static IdempotencyEntry Rehydrate(
        Guid userId,
        string key,
        string requestHash,
        int? statusCode,
        string? responseBody,
        string? contentType,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt) =>
        new()
        {
            UserId = userId,
            Key = key,
            RequestHash = requestHash,
            StatusCode = statusCode,
            ResponseBody = responseBody,
            ContentType = contentType,
            StartedAt = startedAt,
            CompletedAt = completedAt
        };
}
