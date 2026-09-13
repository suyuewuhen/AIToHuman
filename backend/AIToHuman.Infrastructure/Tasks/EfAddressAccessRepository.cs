using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Tasks;

/// <summary>执行地址访问留痕的 EF 实现：只插入与查询，没有更新与删除。</summary>
public sealed class EfAddressAccessRepository(TaskDbContext db) : IAddressAccessRepository
{
    public void Add(AddressAccessEntry entry)
    {
        db.AddressAccessEntries.Add(new AddressAccessRecord
        {
            Id = entry.Id,
            TaskId = entry.TaskId,
            ViewerId = entry.ViewerId,
            ViewerRole = entry.Role.ToString(),
            Outcome = entry.Outcome.ToString(),
            OccurredAt = entry.OccurredAt
        });

        db.SaveChanges();
    }

    public IReadOnlyCollection<AddressAccessEntry> List(Guid? taskId, Guid? viewerId, int limit)
    {
        var query = db.AddressAccessEntries.AsNoTracking();
        if (taskId is { } task) query = query.Where(item => item.TaskId == task);
        if (viewerId is { } viewer) query = query.Where(item => item.ViewerId == viewer);

        return query
            .OrderByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.Id)
            .Take(limit)
            .AsEnumerable()
            .Select(Map)
            .ToArray();
    }

    public int CountDenied(Guid? taskId, Guid? viewerId)
    {
        var query = db.AddressAccessEntries.Where(item => item.Outcome == nameof(AddressAccessOutcome.Denied));
        if (taskId is { } task) query = query.Where(item => item.TaskId == task);
        if (viewerId is { } viewer) query = query.Where(item => item.ViewerId == viewer);
        return query.Count();
    }

    private static AddressAccessEntry Map(AddressAccessRecord record) => AddressAccessEntry.Rehydrate(
        record.Id,
        record.TaskId,
        record.ViewerId,
        Enum.TryParse<AddressAccessRole>(record.ViewerRole, ignoreCase: true, out var role) ? role : AddressAccessRole.Other,
        Enum.TryParse<AddressAccessOutcome>(record.Outcome, ignoreCase: true, out var outcome) ? outcome : AddressAccessOutcome.Denied,
        record.OccurredAt);
}
