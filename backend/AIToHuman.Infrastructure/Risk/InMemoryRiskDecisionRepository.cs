using AIToHuman.Application.Risk;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Infrastructure.Risk;

/// <summary>风险判定留痕的内存实现（无 PostgreSQL 时使用，也用在同一进程的测试里）。</summary>
public sealed class InMemoryRiskDecisionRepository : IRiskDecisionRepository
{
    private readonly List<RiskDecisionEntry> entries = [];
    private readonly Lock gate = new();

    public void Add(RiskDecisionEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
        }
    }

    public IReadOnlyCollection<RiskDecisionEntry> ListByTask(Guid taskId, int limit)
    {
        lock (gate)
        {
            return entries.Where(item => item.TaskId == taskId).OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).Take(limit).ToArray();
        }
    }

    public IReadOnlyCollection<RiskDecisionEntry> ListSince(DateTimeOffset since, int limit)
    {
        lock (gate)
        {
            return entries.Where(item => item.OccurredAt >= since).OrderByDescending(item => item.OccurredAt).Take(limit).ToArray();
        }
    }
}
