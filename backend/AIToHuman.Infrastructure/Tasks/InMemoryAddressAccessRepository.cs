using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Infrastructure.Tasks;

/// <summary>执行地址访问留痕的内存实现（无 PostgreSQL 时使用，也用在同一进程的测试里）。</summary>
public sealed class InMemoryAddressAccessRepository : IAddressAccessRepository
{
    private readonly List<AddressAccessEntry> entries = [];
    private readonly Lock gate = new();

    public void Add(AddressAccessEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
        }
    }

    public IReadOnlyCollection<AddressAccessEntry> List(Guid? taskId, Guid? viewerId, int limit)
    {
        lock (gate)
        {
            return entries
                .Where(item => taskId is null || item.TaskId == taskId)
                .Where(item => viewerId is null || item.ViewerId == viewerId)
                .OrderByDescending(item => item.OccurredAt)
                .ThenByDescending(item => item.Id)
                .Take(limit)
                .ToArray();
        }
    }

    public int CountDenied(Guid? taskId, Guid? viewerId)
    {
        lock (gate)
        {
            return entries
                .Where(item => item.Outcome == AddressAccessOutcome.Denied)
                .Count(item => (taskId is null || item.TaskId == taskId) && (viewerId is null || item.ViewerId == viewerId));
        }
    }
}
