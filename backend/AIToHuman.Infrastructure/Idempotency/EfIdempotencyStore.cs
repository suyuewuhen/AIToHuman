using AIToHuman.Application.Idempotency;
using AIToHuman.Domain.Idempotency;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Idempotency;

/// <summary>幂等记录的 EF 实现：靠 (UserId, Key) 唯一索引把并发占位拦住。</summary>
public sealed class EfIdempotencyStore(TaskDbContext db) : IIdempotencyStore
{
    public IdempotencyEntry? Find(Guid userId, string key) => db.IdempotencyEntries
        .AsNoTracking()
        .SingleOrDefault(item => item.UserId == userId && item.Key == key) is { } record ? Map(record) : null;

    public bool TryStart(IdempotencyEntry entry, out IdempotencyEntry? existing)
    {
        existing = Find(entry.UserId, entry.Key);
        if (existing is not null) return false;

        db.IdempotencyEntries.Add(new IdempotencyRecord
        {
            UserId = entry.UserId,
            Key = entry.Key,
            RequestHash = entry.RequestHash,
            StartedAt = entry.StartedAt
        });

        try
        {
            db.SaveChanges();
            return true;
        }
        catch (DbUpdateException)
        {
            // 并发下另一个请求刚好插进去：清掉本地跟踪状态，读回已有的那条，让调用方按"已存在"处理。
            db.ChangeTracker.Clear();
            existing = Find(entry.UserId, entry.Key);
            return false;
        }
    }

    public void Complete(IdempotencyEntry entry, int statusCode, string? responseBody, string? contentType, DateTimeOffset now)
    {
        // 复用领域判定（超长响应体不缓存），再把结果落到占位行上。
        if (!entry.Complete(statusCode, responseBody, contentType, now))
        {
            Remove(entry);
            return;
        }

        var record = db.IdempotencyEntries.Single(item => item.UserId == entry.UserId && item.Key == entry.Key);
        record.StatusCode = entry.StatusCode;
        record.ResponseBody = entry.ResponseBody;
        record.ContentType = entry.ContentType;
        record.CompletedAt = entry.CompletedAt;
        db.SaveChanges();
    }

    public void Remove(IdempotencyEntry entry)
    {
        var record = db.IdempotencyEntries.SingleOrDefault(item => item.UserId == entry.UserId && item.Key == entry.Key);
        if (record is null) return;
        db.IdempotencyEntries.Remove(record);
        db.SaveChanges();
    }

    /// <summary>
    /// 用一条 DELETE 批量清理（不把记录读进内存）：已完成的看 <c>CompletedAt</c>，
    /// 未完成占位看 <c>StartedAt</c>，两者分开判定。
    /// </summary>
    public int DeleteExpired(DateTimeOffset completedBefore, DateTimeOffset startedBefore, int limit) =>
        db.IdempotencyEntries
            .Where(item => (item.CompletedAt != null && item.CompletedAt < completedBefore)
                || (item.CompletedAt == null && item.StartedAt < startedBefore))
            .OrderBy(item => item.StartedAt)
            .Take(limit)
            .ExecuteDelete();

    private static IdempotencyEntry Map(IdempotencyRecord record) => IdempotencyEntry.Rehydrate(
        record.UserId, record.Key, record.RequestHash, record.StatusCode, record.ResponseBody,
        record.ContentType, record.StartedAt, record.CompletedAt);
}
