using System.Collections.Concurrent;
using AIToHuman.Application.Idempotency;
using AIToHuman.Domain.Idempotency;

namespace AIToHuman.Infrastructure.Idempotency;

/// <summary>内存实现：用 (UserId, Key) 做键，占位与"已存在"的判断在同一把锁里完成。</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<(Guid UserId, string Key), IdempotencyEntry> _entries = new();

    public IdempotencyEntry? Find(Guid userId, string key) =>
        _entries.TryGetValue((userId, key), out var entry) ? entry : null;

    public bool TryStart(IdempotencyEntry entry, out IdempotencyEntry? existing)
    {
        // TryAdd 是原子的：并发请求里只有一个能占到位。
        if (_entries.TryAdd((entry.UserId, entry.Key), entry))
        {
            existing = null;
            return true;
        }

        existing = _entries.TryGetValue((entry.UserId, entry.Key), out var found) ? found : null;
        return false;
    }

    public void Complete(IdempotencyEntry entry, int statusCode, string? responseBody, string? contentType, DateTimeOffset now)
    {
        if (!entry.Complete(statusCode, responseBody, contentType, now))
        {
            Remove(entry);
            return;
        }

        _entries[(entry.UserId, entry.Key)] = entry;
    }

    public void Remove(IdempotencyEntry entry) => _entries.TryRemove((entry.UserId, entry.Key), out _);

    public int DeleteExpired(DateTimeOffset completedBefore, DateTimeOffset startedBefore, int limit)
    {
        var expired = _entries.Values
            .Where(entry => entry.IsCompleted
                ? entry.CompletedAt < completedBefore
                : entry.StartedAt < startedBefore)
            .OrderBy(entry => entry.StartedAt)
            .Take(limit)
            .ToArray();

        var deleted = 0;
        foreach (var entry in expired)
        {
            if (_entries.TryRemove((entry.UserId, entry.Key), out _)) deleted++;
        }

        return deleted;
    }
}
