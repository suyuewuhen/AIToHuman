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

    /// <summary>待处置的申诉队列，按提交时间升序（先来先处理）。</summary>
    public AdminRiskAppealListResponse ListAppeals(int? limit)
    {
        var normalizedLimit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var tasks = taskRepository.ListPendingRiskAppeal(normalizedLimit);
        var owners = userDirectory.FindMany(tasks.Select(item => item.OwnerId).Distinct().ToArray());

        return new(
            tasks.Select(task => Map(task, owners.TryGetValue(task.OwnerId, out var owner) ? owner.Email : null)).ToArray(),
            normalizedLimit);
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

        unitOfWork.Execute(() =>
        {
            taskRepository.Save(task);
            auditRepository.Add(AdminAuditEntry.Record(
                actorId, $"{AppealActionPrefix}.{(accepted ? "accept" : "deny")}", TaskTargetType, task.Id, note, now));
            notifications.EnqueueTaskRiskAppealDecided(task, task.OwnerId, now);
        });

        var owners = userDirectory.FindMany([task.OwnerId]);
        return Map(task, owners.TryGetValue(task.OwnerId, out var owner) ? owner.Email : null);
    }

    private static AdminRiskAppealItemResponse Map(TaskItem task, string? ownerEmail) => new(
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
        CanBeReleasedByAppeal: task.RiskVerdict == RiskVerdict.NeedsReview);
}
