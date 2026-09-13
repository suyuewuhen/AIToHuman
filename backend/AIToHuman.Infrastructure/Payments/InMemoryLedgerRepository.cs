using AIToHuman.Application.Payments;
using AIToHuman.Domain.Payments;

namespace AIToHuman.Infrastructure.Payments;

/// <summary>资金流水的内存实现（无 PostgreSQL 时使用，也用在同一进程的测试里）。</summary>
public sealed class InMemoryLedgerRepository : ILedgerRepository
{
    private readonly List<LedgerEntry> entries = [];
    private readonly Lock gate = new();

    public void Add(LedgerEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
        }
    }

    public IReadOnlyCollection<LedgerEntry> ListByOrder(Guid orderId)
    {
        lock (gate)
        {
            return entries.Where(item => item.OrderId == orderId).OrderBy(item => item.OccurredAt).ThenBy(item => item.Id).ToArray();
        }
    }

    public IReadOnlyCollection<LedgerEntry> ListRecent(int limit)
    {
        lock (gate)
        {
            return entries.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id).Take(limit).ToArray();
        }
    }
}
