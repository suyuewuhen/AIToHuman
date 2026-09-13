using AIToHuman.Application.Risk;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Risk;

/// <summary>风险判定留痕的 EF 实现：只插入与查询，没有更新与删除。</summary>
public sealed class EfRiskDecisionRepository(TaskDbContext db) : IRiskDecisionRepository
{
    public void Add(RiskDecisionEntry entry)
    {
        db.RiskDecisionEntries.Add(new RiskDecisionRecord
        {
            Id = entry.Id,
            TaskId = entry.TaskId,
            Reason = entry.Reason.ToString(),
            Verdict = entry.Verdict.ToString(),
            RuleCode = entry.RuleCode,
            Category = entry.Category,
            RuleVersion = entry.RuleVersion,
            RewardAmount = entry.RewardAmount,
            OccurredAt = entry.OccurredAt
        });

        db.SaveChanges();
    }

    public IReadOnlyCollection<RiskDecisionEntry> ListByTask(Guid taskId, int limit) => db.RiskDecisionEntries
        .AsNoTracking()
        .Where(item => item.TaskId == taskId)
        .OrderBy(item => item.OccurredAt)
        .ThenBy(item => item.Id)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public IReadOnlyCollection<RiskDecisionEntry> ListSince(DateTimeOffset since, int limit) => db.RiskDecisionEntries
        .AsNoTracking()
        .Where(item => item.OccurredAt >= since)
        .OrderByDescending(item => item.OccurredAt)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    private static RiskDecisionEntry Map(RiskDecisionRecord record) => RiskDecisionEntry.Rehydrate(
        record.Id,
        record.TaskId,
        Enum.TryParse<RiskDecisionReason>(record.Reason, ignoreCase: true, out var reason) ? reason : RiskDecisionReason.Created,
        Enum.TryParse<RiskVerdict>(record.Verdict, ignoreCase: true, out var verdict) ? verdict : RiskVerdict.Allowed,
        record.RuleCode,
        record.Category,
        record.RuleVersion,
        record.RewardAmount,
        record.OccurredAt);
}

/// <summary>申诉留档里的误伤统计（EF 实现）：在数据库上按原因代码分组计数，不把记录拉回内存。</summary>
public sealed class EfRiskAppealStatistics(TaskDbContext db) : IRiskAppealStatistics
{
    public IReadOnlyDictionary<string, int> CountAcceptedByRuleCode(DateTimeOffset since) => db.RiskAppeals
        .AsNoTracking()
        .Where(item => item.Status == nameof(RiskAppealStatus.Accepted) && item.DecidedAt != null && item.DecidedAt >= since && item.RuleCode != null)
        .GroupBy(item => item.RuleCode!)
        .Select(group => new { Code = group.Key, Count = group.Count() })
        .ToDictionary(item => item.Code, item => item.Count);
}
