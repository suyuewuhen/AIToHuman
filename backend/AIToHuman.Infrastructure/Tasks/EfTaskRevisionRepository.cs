using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Tasks;

/// <summary>草稿版本历史的 EF 实现：只插入与查询，没有更新与删除。</summary>
public sealed class EfTaskRevisionRepository(TaskDbContext db) : ITaskRevisionRepository
{
    public IReadOnlyCollection<TaskDraftRevision> ListByTask(Guid taskId) => db.TaskRevisions
        .AsNoTracking()
        .Where(item => item.TaskId == taskId)
        .OrderBy(item => item.Revision)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public int LatestRevision(Guid taskId) => db.TaskRevisions
        .Where(item => item.TaskId == taskId)
        .Select(item => (int?)item.Revision)
        .Max() ?? 0;

    public void Add(TaskDraftRevision revision)
    {
        db.TaskRevisions.Add(ToRecord(revision));
        db.SaveChanges();
    }

    private static TaskRevisionRecord ToRecord(TaskDraftRevision revision) => new()
    {
        Id = revision.Id,
        TaskId = revision.TaskId,
        Revision = revision.Revision,
        Title = revision.Title,
        Description = revision.Description,
        District = revision.District,
        Deadline = revision.Deadline,
        RewardAmount = revision.RewardAmount,
        RewardCurrency = revision.RewardCurrency,
        AcceptanceCriteriaJson = revision.AcceptanceCriteriaJson,
        ExecutionAddress = revision.ExecutionAddress,
        ApplicationDeadline = revision.ApplicationDeadline,
        RiskVerdict = revision.RiskVerdict,
        RiskRuleCode = revision.RiskRuleCode,
        RiskRuleVersion = revision.RiskRuleVersion,
        EditedBy = revision.EditedBy,
        ChangeSummary = revision.ChangeSummary,
        CreatedAt = revision.CreatedAt
    };

    private static TaskDraftRevision Map(TaskRevisionRecord record) => TaskDraftRevision.Rehydrate(
        record.Id, record.TaskId, record.Revision, record.Title, record.Description, record.District,
        record.Deadline, record.RewardAmount, record.RewardCurrency, record.AcceptanceCriteriaJson,
        record.ExecutionAddress, record.ApplicationDeadline, record.RiskVerdict, record.RiskRuleCode,
        record.RiskRuleVersion, record.EditedBy, record.ChangeSummary, record.CreatedAt);
}
