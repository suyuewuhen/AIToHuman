using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 发布后的风险复检：把"判定只发生在草稿阶段"这个缺口补上。
///
/// 触发有两处：
/// 一是**规则升级**——运营改完词表或阈值，已经在线的任务是按旧版规则判定过的，必须重新判一次
/// （后台任务周期扫描，运营也可以手动触发一轮，见 <see cref="Recheck"/>）；
/// 二是**加价**——悬赏本身就是规则输入（高金额转人工），加价之后立刻重判
/// （见 <c>TaskService.IncreaseReward</c>，它复用这里的 <see cref="ApplyOutcome"/>）。
///
/// 处置口径（与领域层的 <see cref="RiskEnforcementOutcome"/> 一致）：
/// 命中禁止类别且没有订单 → 自动下架并通知所有者与报名中的服务者；已经有订单 → 冻结订单
/// （平台发起争议，进运营的争议队列）；只命中"需人工复核" → 任务保持在线、进运营复核队列。
/// 所有会改变用户处境的处置都写一条运营审计（动作 <c>task.risk.recheck.*</c>，操作人是平台自己）。
/// </summary>
public sealed class RiskEnforcementService(
    ITaskRepository taskRepository,
    IOrderRepository orderRepository,
    IAdminAuditRepository auditRepository,
    NotificationService notifications,
    IRiskRuleCatalogProvider catalogs,
    TimeProvider timeProvider,
    IUnitOfWork unitOfWork,
    RiskDecisionService? riskDecisions = null)
{
    /// <summary>自动下架的审计动作。</summary>
    public const string UnpublishAction = "task.risk.recheck.unpublish";

    /// <summary>冻结订单的审计动作。</summary>
    public const string FreezeOrderAction = "task.risk.recheck.freezeOrder";

    /// <summary>审计里的对象类型（与运营处置任务时一致）。</summary>
    public const string TargetType = "task";

    /// <summary>单轮复检最多处理多少条任务，避免一次启动就长时间占用数据库连接。</summary>
    public const int DefaultBatchSize = 200;

    /// <summary>
    /// 跑一轮发布后复检。返回本轮各处置结果的数量，供后台任务打日志、也供运营手动触发时看结果。
    /// 单条任务失败（例如订单刚被别处改成不可冻结的状态、或状态在扫描期间被改动）只跳过它，不影响整轮。
    /// </summary>
    public RiskRecheckResult Recheck(int? limit = null)
    {
        var batchSize = Math.Clamp(limit ?? DefaultBatchSize, 1, DefaultBatchSize);
        var catalog = catalogs.GetEffective();
        var candidates = taskRepository.ListRiskRecheckCandidates(catalog.Version, batchSize);

        var refreshed = 0;
        var flagged = 0;
        var unpublished = 0;
        var frozen = 0;
        var skipped = 0;

        foreach (var task in candidates)
        {
            Order? order = null;
            if (task.Status == TaskStatus.Assigned)
            {
                order = orderRepository.GetByTask(task.Id);
                // 读不到订单、或订单已经结束/取消/在争议里：留给下一轮，不做半截处置。
                if (order is null || !order.CanBeSuspendedForRisk)
                {
                    skipped++;
                    continue;
                }
            }

            var now = timeProvider.GetUtcNow();
            RiskEnforcementOutcome outcome;
            try
            {
                outcome = task.ReassessRisk(catalog, now);
            }
            catch (DomainException)
            {
                // 任务状态在扫描期间被别的操作改掉了（例如刚被验收关闭）：跳过，下一轮不会再有它。
                skipped++;
                continue;
            }

            switch (outcome)
            {
                case RiskEnforcementOutcome.Unchanged:
                    refreshed++;
                    unitOfWork.Execute(() =>
                    {
                        taskRepository.Save(task);
                        // 复检也算一次判定：统计里能看出"规则升级后重判了多少条、其中命中多少"。
                        riskDecisions?.Record(task, RiskDecisionReason.Rechecked, now);
                    });
                    break;

                case RiskEnforcementOutcome.FlaggedForRecheck:
                    flagged++;
                    unitOfWork.Execute(() =>
                    {
                        taskRepository.Save(task);
                        riskDecisions?.Record(task, RiskDecisionReason.Rechecked, now);
                        notifications.EnqueueTaskRiskEnforced(task, task.OwnerId, now);
                    });
                    break;

                case RiskEnforcementOutcome.Unpublished:
                    unpublished++;
                    var applicants = task.Applications
                        .Where(item => item.Status == TaskApplicationStatus.Pending)
                        .Select(item => item.WorkerId)
                        .ToArray();
                    unitOfWork.Execute(() =>
                    {
                        taskRepository.Save(task);
                        riskDecisions?.Record(task, RiskDecisionReason.Rechecked, now);
                        auditRepository.Add(AdminAuditEntry.Record(
                            AdminAuditEntry.SystemActorId, UnpublishAction, TargetType, task.Id, task.RiskEnforcementReason ?? "风险复检", now));
                        notifications.EnqueueTaskRiskEnforced(task, task.OwnerId, now);
                        notifications.EnqueueTaskCancelled(task, task.OwnerId, now);
                        foreach (var applicant in applicants) notifications.EnqueueTaskCancelled(task, applicant, now);
                    });
                    break;

                case RiskEnforcementOutcome.OrderFrozen:
                    frozen++;
                    var frozenOrder = order!;
                    frozenOrder.SuspendByRisk($"任务在发布后复检中命中平台禁止的类别：{task.RiskEnforcementReason}", now);
                    unitOfWork.Execute(() =>
                    {
                        taskRepository.Save(task);
                        riskDecisions?.Record(task, RiskDecisionReason.Rechecked, now);
                        orderRepository.Save(frozenOrder);
                        auditRepository.Add(AdminAuditEntry.Record(
                            AdminAuditEntry.SystemActorId, FreezeOrderAction, TargetType, task.Id, task.RiskEnforcementReason ?? "风险复检", now));
                        notifications.EnqueueTaskRiskEnforced(task, task.OwnerId, now);
                        // 平台发起的冻结走既有争议路径：双方各自收到"订单进入争议"，运营在争议队列里处置。
                        notifications.EnqueueOrderSuspendedByRisk(frozenOrder, frozenOrder.OwnerId, now);
                        notifications.EnqueueOrderSuspendedByRisk(frozenOrder, frozenOrder.WorkerId, now);
                    });
                    break;
            }
        }

        return new RiskRecheckResult(catalog.Version, candidates.Count, refreshed, flagged, unpublished, frozen, skipped);
    }

    /// <summary>
    /// 把一次复检结论的副作用做掉：通知、审计，以及（禁止类别且没有订单时的）下架通知。
    /// 调用方负责在同一条 <see cref="IUnitOfWork"/> 里保存任务（见 <c>TaskService.IncreaseReward</c>），
    /// 这样"任务被标成待复检"和"用户收到通知"要么都成立、要么都不成立。
    ///
    /// 需要冻结订单的情形不在这里处理：冻结订单必须由扫描路径一并改订单状态，
    /// 半截处置（任务标记了、订单没冻结）比什么都不做更糟。
    /// </summary>
    public void ApplyOutcome(TaskItem task, RiskEnforcementOutcome outcome, DateTimeOffset now)
    {
        switch (outcome)
        {
            case RiskEnforcementOutcome.Unchanged:
                return;

            case RiskEnforcementOutcome.FlaggedForRecheck:
                notifications.EnqueueTaskRiskEnforced(task, task.OwnerId, now);
                return;

            case RiskEnforcementOutcome.Unpublished:
                auditRepository.Add(AdminAuditEntry.Record(
                    AdminAuditEntry.SystemActorId, UnpublishAction, TargetType, task.Id, task.RiskEnforcementReason ?? "风险复检", now));
                notifications.EnqueueTaskRiskEnforced(task, task.OwnerId, now);
                notifications.EnqueueTaskCancelled(task, task.OwnerId, now);
                return;

            default:
                throw new InvalidOperationException(
                    "冻结订单必须由 RiskEnforcementService.Recheck 完成（它会在同一个工作单元里改订单状态）。");
        }
    }
}

/// <summary>一轮发布后复检的结果。<paramref name="RuleVersion"/> 是本轮用的规则版本。</summary>
public sealed record RiskRecheckResult(
    int RuleVersion,
    int Scanned,
    int Refreshed,
    int Flagged,
    int Unpublished,
    int Frozen,
    int Skipped)
{
    public AdminRiskRecheckResponse ToResponse() => new(RuleVersion, Scanned, Refreshed, Flagged, Unpublished, Frozen, Skipped);
}
