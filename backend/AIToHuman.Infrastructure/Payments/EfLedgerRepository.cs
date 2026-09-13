using AIToHuman.Application.Payments;
using AIToHuman.Domain.Payments;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Payments;

/// <summary>资金流水的 EF 实现：只插入与查询，没有更新与删除（账本一旦写下就不许改）。</summary>
public sealed class EfLedgerRepository(TaskDbContext db) : ILedgerRepository
{
    public void Add(LedgerEntry entry)
    {
        db.LedgerEntries.Add(new LedgerEntryRecord
        {
            Id = entry.Id,
            OrderId = entry.OrderId,
            TaskId = entry.TaskId,
            DebitAccount = entry.DebitAccount.ToString(),
            CreditAccount = entry.CreditAccount.ToString(),
            Amount = entry.Amount,
            Currency = entry.Currency,
            Kind = entry.Kind.ToString(),
            Note = entry.Note,
            OccurredAt = entry.OccurredAt
        });

        db.SaveChanges();
    }

    public IReadOnlyCollection<LedgerEntry> ListByOrder(Guid orderId) => db.LedgerEntries
        .AsNoTracking()
        .Where(item => item.OrderId == orderId)
        .OrderBy(item => item.OccurredAt)
        .ThenBy(item => item.Id)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public IReadOnlyCollection<LedgerEntry> ListRecent(int limit) => db.LedgerEntries
        .AsNoTracking()
        .OrderByDescending(item => item.OccurredAt)
        .ThenByDescending(item => item.Id)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    private static LedgerEntry Map(LedgerEntryRecord record) => LedgerEntry.Rehydrate(
        record.Id,
        record.OrderId,
        record.TaskId,
        Enum.TryParse<LedgerAccount>(record.DebitAccount, ignoreCase: true, out var debit) ? debit : LedgerAccount.Escrow,
        Enum.TryParse<LedgerAccount>(record.CreditAccount, ignoreCase: true, out var credit) ? credit : LedgerAccount.Escrow,
        record.Amount,
        record.Currency,
        Enum.TryParse<LedgerEntryKind>(record.Kind, ignoreCase: true, out var kind) ? kind : LedgerEntryKind.Hold,
        record.Note,
        record.OccurredAt);
}
