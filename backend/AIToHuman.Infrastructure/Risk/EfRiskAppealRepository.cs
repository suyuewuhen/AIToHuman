using AIToHuman.Application.Risk;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Risk;

/// <summary>
/// 申诉留档的 EF 实现：提交时插入一行，结论写回同一行；没有更新历史行、也没有删除。
/// 节流用的两个计数都在数据库上算（COUNT），不把记录拉回内存再数。
/// </summary>
public sealed class EfRiskAppealRepository(TaskDbContext db) : IRiskAppealRepository
{
    public void Add(RiskAppealRecord record)
    {
        db.RiskAppeals.Add(ToRecord(record));
        db.SaveChanges();
    }

    public void Save(RiskAppealRecord record)
    {
        var existing = db.RiskAppeals.Single(item => item.Id == record.Id);
        existing.Status = record.Status.ToString();
        existing.DecidedBy = record.DecidedBy;
        existing.DecidedAt = record.DecidedAt;
        existing.DecisionNote = record.DecisionNote;
        db.SaveChanges();
    }

    public IReadOnlyCollection<RiskAppealRecord> ListByTask(Guid taskId) => db.RiskAppeals
        .AsNoTracking()
        .Where(item => item.TaskId == taskId)
        .OrderBy(item => item.SubmittedAt)
        .ThenBy(item => item.Id)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public int CountByTask(Guid taskId) => db.RiskAppeals.Count(item => item.TaskId == taskId);

    public IReadOnlyDictionary<Guid, int> CountByTasks(IReadOnlyCollection<Guid> taskIds) => db.RiskAppeals
        .Where(item => taskIds.Contains(item.TaskId))
        .GroupBy(item => item.TaskId)
        .Select(group => new { TaskId = group.Key, Count = group.Count() })
        .ToDictionary(item => item.TaskId, item => item.Count);

    public int CountByOwnerSince(Guid ownerId, DateTimeOffset since) =>
        db.RiskAppeals.Count(item => item.OwnerId == ownerId && item.SubmittedAt >= since);

    public RiskAppealRecord? FindPending(Guid taskId) => db.RiskAppeals
        .Where(item => item.TaskId == taskId && item.Status == nameof(RiskAppealStatus.Pending))
        .OrderBy(item => item.SubmittedAt)
        .FirstOrDefault() is { } record ? Map(record) : null;

    private static RiskAppealRecordRecord ToRecord(RiskAppealRecord record) => new()
    {
        Id = record.Id,
        TaskId = record.TaskId,
        OwnerId = record.OwnerId,
        RuleCode = record.RuleCode,
        RuleVersion = record.RuleVersion,
        Verdict = record.Verdict.ToString(),
        Reason = record.Reason,
        SubmittedAt = record.SubmittedAt,
        Status = record.Status.ToString(),
        DecidedBy = record.DecidedBy,
        DecidedAt = record.DecidedAt,
        DecisionNote = record.DecisionNote
    };

    private static RiskAppealRecord Map(RiskAppealRecordRecord record) => RiskAppealRecord.Rehydrate(
        record.Id, record.TaskId, record.OwnerId, record.RuleCode, record.RuleVersion,
        Enum.TryParse<RiskVerdict>(record.Verdict, ignoreCase: true, out var verdict) ? verdict : RiskVerdict.Allowed,
        record.Reason, record.SubmittedAt,
        Enum.TryParse<RiskAppealStatus>(record.Status, ignoreCase: true, out var status) ? status : RiskAppealStatus.Pending,
        record.DecidedBy, record.DecidedAt, record.DecisionNote);
}
