using AIToHuman.Application.Risk;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Infrastructure.Risk;

/// <summary>申诉留档的内存实现（无 PostgreSQL 时使用，也用在同一进程的测试里）。</summary>
public sealed class InMemoryRiskAppealRepository : IRiskAppealRepository
{
    private readonly List<RiskAppealRecord> records = [];
    private readonly Lock gate = new();

    public void Add(RiskAppealRecord record)
    {
        lock (gate)
        {
            records.Add(record);
        }
    }

    public void Save(RiskAppealRecord record)
    {
        // 内存实现保存的是同一个对象引用，写回是空操作；这里只做一次存在性检查，避免"保存了不存在的记录"。
        lock (gate)
        {
            if (!records.Any(item => item.Id == record.Id)) throw new InvalidOperationException("申诉记录不存在。");
        }
    }

    public IReadOnlyCollection<RiskAppealRecord> ListByTask(Guid taskId)
    {
        lock (gate)
        {
            return records.Where(item => item.TaskId == taskId).OrderBy(item => item.SubmittedAt).ThenBy(item => item.Id).ToArray();
        }
    }

    public int CountByTask(Guid taskId)
    {
        lock (gate)
        {
            return records.Count(item => item.TaskId == taskId);
        }
    }

    public IReadOnlyDictionary<Guid, int> CountByTasks(IReadOnlyCollection<Guid> taskIds)
    {
        lock (gate)
        {
            return records.Where(item => taskIds.Contains(item.TaskId))
                .GroupBy(item => item.TaskId)
                .ToDictionary(group => group.Key, group => group.Count());
        }
    }

    public int CountByOwnerSince(Guid ownerId, DateTimeOffset since)
    {
        lock (gate)
        {
            return records.Count(item => item.OwnerId == ownerId && item.SubmittedAt >= since);
        }
    }

    public RiskAppealRecord? FindPending(Guid taskId)
    {
        lock (gate)
        {
            return records
                .Where(item => item.TaskId == taskId && item.Status == RiskAppealStatus.Pending)
                .OrderBy(item => item.SubmittedAt)
                .FirstOrDefault();
        }
    }
}
