using System.Text.Json;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Infrastructure.Tasks;

public sealed class EfTaskRepository(TaskDbContext db) : ITaskRepository
{
    public IReadOnlyCollection<TaskItem> ListPublished(PublishedTaskFilter filter)
    {
        var query = db.Tasks
            .AsNoTracking()
            .Include(task => task.Applications)
            .Where(task => task.Status == nameof(DomainTaskStatus.Published));

        if (filter.District is { } district) query = query.Where(task => task.District == district);
        if (filter.MinReward is { } minReward) query = query.Where(task => task.RewardAmount >= minReward);
        if (filter.MaxReward is { } maxReward) query = query.Where(task => task.RewardAmount <= maxReward);
        if (filter.CursorDeadline is { } deadline && filter.CursorId is { } cursorId)
        {
            // 与排序键保持一致：先比截止时间，同一时刻再用 Id 兜底，避免分页丢条或重复。
            query = query.Where(task => task.Deadline > deadline || (task.Deadline == deadline && task.Id.CompareTo(cursorId) > 0));
        }

        return query
            .OrderBy(task => task.Deadline)
            .ThenBy(task => task.Id)
            .Take(filter.Limit)
            .AsEnumerable()
            .Select(Map)
            .ToArray();
    }

    public IReadOnlyCollection<TaskItem> ListByOwner(Guid ownerId) => db.Tasks
        .AsNoTracking()
        .Include(task => task.Applications)
        .Where(task => task.OwnerId == ownerId)
        .OrderByDescending(task => task.CreatedAt)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    /// <summary>服务者视角的“我的报名”：只要报过名就出现（含被拒绝、已撤回、已失效的记录）。</summary>
    public IReadOnlyCollection<TaskItem> ListByApplicant(Guid workerId, int limit) => db.Tasks
        .AsNoTracking()
        .Include(task => task.Applications)
        .Where(task => task.Applications.Any(application => application.WorkerId == workerId))
        .OrderByDescending(task => task.CreatedAt)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    /// <summary>
    /// 后台过期扫描：过截止时间但仍处于 Published 的任务。保持跟踪，Save 时才能用读到的版本做并发校验，
    /// 两个实例同时扫到同一条时只有一个能写成功，另一个抛并发冲突并留给下一轮。
    /// </summary>
    public IReadOnlyCollection<TaskItem> ListOverduePublished(DateTimeOffset now, int limit) => db.Tasks
        .Include(task => task.Applications)
        .Where(task => task.Status == nameof(DomainTaskStatus.Published) && task.Deadline <= now)
        .OrderBy(task => task.Deadline)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    /// <summary>
    /// 运营风险复核队列：等人工复核的任务，按创建时间升序（先来先处理）。
    /// 与过期扫描一样保持跟踪，处置时保存会用读到的版本做并发校验，两个运营同时处置只有一个人能成功。
    /// </summary>
    public IReadOnlyCollection<TaskItem> ListPendingRiskReview(int limit) => db.Tasks
        .Include(task => task.Applications)
        .Where(task => task.RiskReviewStatus == nameof(RiskReviewStatus.Pending))
        .OrderBy(task => task.CreatedAt)
        .ThenBy(task => task.Id)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    // 注意：Get 必须保持跟踪状态。乐观并发令牌依赖「做业务判断时读到的版本」与
    // Save 时 WHERE 里用的原始版本是同一个；一旦这里用 AsNoTracking，Save 会重新读库拿到
    // 最新版本再自增，并发写入就永远不会冲突。
    public TaskItem? Get(Guid id) => db.Tasks
        .Include(task => task.Applications)
        .SingleOrDefault(task => task.Id == id) is { } record ? Map(record) : null;

    public void Add(TaskItem task)
    {
        db.Tasks.Add(ToRecord(task));
        db.SaveChanges();
    }

    public void Save(TaskItem task)
    {
        var record = db.Tasks.Single(item => item.Id == task.Id);

        // 用 SetValues 整体覆盖，而不是逐列手写复制：草稿字段现在是可编辑的，
        // 手写列表极容易漏掉某一列（本仓库就出现过“编辑草稿后正文没落库”的真机缺陷——
        // 内存仓储保存的是同一个对象引用，所以单元测试完全看不出来）。
        // 这样写还有一个好处：以后给 tasks 加列，只要 ToRecord 填了值就会自动带上。
        var version = record.Version;
        db.Entry(record).CurrentValues.SetValues(ToRecord(task));
        // 并发令牌不参与覆盖：下面按加载时的原值自增，EF 会用它做 WHERE。
        record.Version = version + 1;

        var storedApplications = db.Applications.Where(item => item.TaskId == task.Id).ToDictionary(item => item.Id);
        foreach (var current in task.Applications)
        {
            if (storedApplications.TryGetValue(current.Id, out var existing))
            {
                existing.Status = current.Status.ToString();
                existing.Note = current.Note;
                existing.SubmittedAt = current.SubmittedAt;
            }
            else
            {
                db.Applications.Add(ToRecord(current, task.Id));
            }
        }

        db.SaveChanges();
    }

    private static TaskRecord ToRecord(TaskItem task) => new()
    {
        Id = task.Id, OwnerId = task.OwnerId, Title = task.Title, Description = task.Description, District = task.District,
        ExecutionAddress = task.ExecutionAddress,
        Deadline = task.Deadline, RewardAmount = task.Reward.Amount, RewardCurrency = task.Reward.Currency,
        Status = task.Status.ToString(), AcceptanceCriteriaJson = JsonSerializer.Serialize(task.AcceptanceCriteria), CreatedAt = task.CreatedAt,
        ExpiredAt = task.ExpiredAt, CancelledAt = task.CancelledAt, CancellationReason = task.CancellationReason,
        ApplicationDeadline = task.ApplicationDeadline,
        RiskVerdict = task.RiskVerdict.ToString(), RiskRuleCode = task.RiskRuleCode, RiskCategory = task.RiskCategory,
        RiskSummary = task.RiskSummary, RiskRuleVersion = task.RiskRuleVersion, RiskAssessedAt = task.RiskAssessedAt,
        RiskReviewStatus = task.RiskReviewStatus.ToString(), RiskReviewedBy = task.RiskReviewedBy,
        RiskReviewedAt = task.RiskReviewedAt, RiskReviewNote = task.RiskReviewNote,
        Applications = task.Applications.Select(item => ToRecord(item, task.Id)).ToList()
    };

    private static ApplicationRecord ToRecord(TaskApplication item, Guid taskId) => new()
    {
        Id = item.Id, TaskId = taskId, WorkerId = item.WorkerId, Note = item.Note, SubmittedAt = item.SubmittedAt, Status = item.Status.ToString()
    };

    private static TaskItem Map(TaskRecord record) => TaskItem.Rehydrate(
        record.Id, record.OwnerId, record.Title, record.Description, record.District, record.Deadline,
        new Money(record.RewardAmount, record.RewardCurrency), JsonSerializer.Deserialize<string[]>(record.AcceptanceCriteriaJson) ?? [], record.CreatedAt,
        Enum.Parse<DomainTaskStatus>(record.Status), record.Applications.Select(item => TaskApplication.Rehydrate(item.Id, item.WorkerId, item.Note, item.SubmittedAt, Enum.Parse<TaskApplicationStatus>(item.Status))), record.ExecutionAddress,
        record.ExpiredAt, record.CancelledAt, record.CancellationReason, record.ApplicationDeadline,
        ParseRiskVerdict(record.RiskVerdict), record.RiskRuleCode, record.RiskCategory, record.RiskSummary,
        record.RiskRuleVersion, record.RiskAssessedAt, ParseRiskReviewStatus(record.RiskReviewStatus),
        record.RiskReviewedBy, record.RiskReviewedAt, record.RiskReviewNote);

    /// <summary>
    /// 风险列是后加的，历史行可能是不认识或空的值。解析失败一律按“放行且无需复核”处理：
    /// 规则判定本来就会在下次发布时重跑，读失败不该让整条任务读不出来。
    /// </summary>
    private static RiskVerdict ParseRiskVerdict(string? value) =>
        Enum.TryParse<RiskVerdict>(value, ignoreCase: true, out var parsed) ? parsed : RiskVerdict.Allowed;

    private static RiskReviewStatus ParseRiskReviewStatus(string? value) =>
        Enum.TryParse<RiskReviewStatus>(value, ignoreCase: true, out var parsed) ? parsed : RiskReviewStatus.NotRequired;
}
