using AIToHuman.Domain.Idempotency;

namespace AIToHuman.Application.Idempotency;

/// <summary>
/// 幂等记录的存取。占位用 <see cref="TryStart"/>：它必须依赖"同一用户 + 同一个键"的唯一约束，
/// 这样两个并发请求里只有第一个能占到位，第二个拿到已存在的记录并据此回放或报冲突。
/// </summary>
public interface IIdempotencyStore
{
    IdempotencyEntry? Find(Guid userId, string key);

    /// <summary>占位成功返回 true；已被占用返回 false 并通过 <paramref name="existing"/> 给出已有记录。</summary>
    bool TryStart(IdempotencyEntry entry, out IdempotencyEntry? existing);

    void Complete(IdempotencyEntry entry, int statusCode, string? responseBody, string? contentType, DateTimeOffset now);

    /// <summary>丢掉占位（业务抛异常、响应太大、或占位已过期时用）。</summary>
    void Remove(IdempotencyEntry entry);
}
