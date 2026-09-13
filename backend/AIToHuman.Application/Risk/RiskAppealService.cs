using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 误拦申诉的运营侧：看待处置队列、给出处置结论。
/// 所有者提交申诉走 <see cref="TaskService.OpenRiskAppeal"/>（复用任务载荷的映射）。
///
/// 处置能力刻意分两档（这是本功能最重要的产品决定，也是安全边界）：
/// - 转人工后被驳回的任务：申诉成立即放行——运营本来就有这个权限，申诉只是多一双眼睛；
/// - 被禁止类别命中的任务：申诉成立也**不会**获得发布许可，只留下"规则误伤"的结论，
///   提示所有者修改文案后重新判定（改动会自动重判，不再命中就自然放行）。
///   平台红线不能因为多了一个申诉入口就被绕过去。
/// </summary>
public sealed class RiskAppealService(
    ITaskRepository taskRepository,
    IRiskAppealRepository appealRepository,
    IAdminAuditRepository auditRepository,
    IUserDirectory userDirectory,
    TimeProvider timeProvider,
    NotificationService notifications,
    IUnitOfWork unitOfWork)
{
    /// <summary>申诉处置的审计动作前缀，完整动作形如 <c>task.risk.appeal.accept</c>。</summary>
    public const string AppealActionPrefix = "task.risk.appeal";
    public const string TaskTargetType = "task";
    public const int MaxLimit = 100;
    public const int DefaultLimit = 20;

    /// <summary>待处置的申诉队列，按提交时间升序（先来先处理）。每一项带上"这条任务累计申诉过几次"。</summary>
    public AdminRiskAppealListResponse ListAppeals(int? limit)
    {
        var normalizedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var tasks = taskRepository.ListPendingRiskAppeal(normalizedLimit);
        var owners = userDirectory.FindMany(tasks.Select(item => item.OwnerId).Distinct().ToArray());
        var counts = appealRepository.CountByTasks(tasks.Select(item => item.Id).ToArray());

        return new(
            tasks.Select(task => Map(
                task,
                owners.TryGetValue(task.OwnerId, out var owner) ? owner.Email : null,
                counts.GetValueOrDefault(task.Id))).ToArray(),
            normalizedLimit);
    }

    /// <summary>
    /// 某条任务的完整申诉轨迹（提交理由、提交时的规则与结论、运营结论与依据）。
    /// 任务上只留最新一次申诉状态，想看"被误拦过几次"要看这里。
    /// </summary>
    public AdminRiskAppealHistoryResponse ListHistory(Guid taskId)
    {
        if (taskRepository.Get(taskId) is null) throw new KeyNotFoundException("任务不存在。");

        var records = appealRepository.ListByTask(taskId);
        var deciders = userDirectory.FindMany(records.Where(item => item.DecidedBy is not null).Select(item => item.DecidedBy!.Value).Distinct().ToArray());

        return new(
            taskId,
            records.Select(record => new AdminRiskAppealRecordResponse(
                record.Id,
                record.RuleCode,
                record.RuleVersion,
                record.Verdict.ToString(),
                record.Reason,
                record.SubmittedAt,
                record.Status.ToString(),
                record.DecidedBy,
                record.DecidedBy is { } decider && deciders.TryGetValue(decider, out var view) ? view.DisplayName : null,
                record.DecidedAt,
                record.DecisionNote)).ToArray(),
            RiskAppealPolicy.MaxPerTask,
            RiskAppealPolicy.MaxPerOwnerPerDay);
    }

    /// <summary>
    /// 处置申诉：<paramref name="accepted"/> 为真表示运营认为原判定是误伤。
    /// 依据必填并写进运营审计；"禁止类别不因申诉放行"由领域层保证。
    /// </summary>
    public AdminRiskAppealItemResponse DecideAppeal(Guid taskId, bool accepted, string note, Guid actorId)
    {
        var task = taskRepository.Get(taskId) ?? throw new KeyNotFoundException("任务不存在。");
        var now = timeProvider.GetUtcNow();

        // 先让领域规则判定能不能处置（没有待处置申诉、没写依据都在这里拦），成立之后才写审计。
        task.ResolveRiskAppeal(actorId, accepted, note, now);
        // 留档里的那一行也要写上结论；老数据（这张表之前提交的申诉）没有对应行时跳过。
        var record = appealRepository.FindPending(taskId);
        record?.Decide(actorId, accepted, note, now);

        unitOfWork.Execute(() =>
        {
            taskRepository.Save(task);
            if (record is not null) appealRepository.Save(record);
            auditRepository.Add(AdminAuditEntry.Record(
                actorId, $"{AppealActionPrefix}.{(accepted ? "accept" : "deny")}", TaskTargetType, task.Id, note, now));
            notifications.EnqueueTaskRiskAppealDecided(task, task.OwnerId, now);
        });

        var owners = userDirectory.FindMany([task.OwnerId]);
        return Map(task, owners.TryGetValue(task.OwnerId, out var owner) ? owner.Email : null, appealRepository.CountByTask(task.Id));
    }

    private static AdminRiskAppealItemResponse Map(TaskItem task, string? ownerEmail, int appealCount) => new(
        task.Id,
        task.Title,
        task.Description,
        task.District,
        task.Reward.Amount,
        task.Reward.Currency,
        task.OwnerId,
        ownerEmail,
        task.RiskVerdict.ToString(),
        task.RiskRuleCode,
        task.RiskCategory,
        task.RiskSummary,
        task.RiskRuleVersion,
        task.RiskReviewStatus.ToString(),
        task.RiskReviewNote,
        task.RiskAppealStatus.ToString(),
        task.RiskAppealReason,
        task.RiskAppealedAt,
        // 禁止类别即使申诉成立也不会因此可发布：这个字段明确告诉运营"放行能力有没有生效"。
        CanBeReleasedByAppeal: task.RiskVerdict == RiskVerdict.NeedsReview,
        AppealCount: appealCount);
}
