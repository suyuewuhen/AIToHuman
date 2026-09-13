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

namespace AIToHuman.Application.Admin;

/// <summary>
/// 运营后台：跨所有者检索任务与用户、查看任务详情、把尚未分配的任务下架并留审计，
/// 以及处置风险复核队列（放行或驳回）。
/// 风险判定本身是领域层的确定性规则（<see cref="RiskRuleCatalog"/>，规则目录的查看与编辑见
/// <c>RiskRuleCatalogService</c>）；这里只做人能做的事：对“需要人工复核”的任务给出结论。
/// 被禁止的类别不会进队列，人工也无权放行。
/// </summary>
public sealed class AdminConsoleService(
    IAdminTaskQuery taskQuery,
    IAdminOrderQuery orderQuery,
    IUserDirectory userDirectory,
    ITaskRepository taskRepository,
    IOrderRepository orderRepository,
    IAdminAuditRepository auditRepository,
    TimeProvider timeProvider,
    NotificationService notifications,
    IUnitOfWork unitOfWork)
{
    /// <summary>运营下架动作的审计名称。</summary>
    public const string TaskCancelAction = "task.cancel";
    public const string TaskTargetType = "task";

    /// <summary>处置争议的审计动作前缀，完整动作形如 <c>order.dispute.approve</c>。</summary>
    public const string OrderResolveActionPrefix = "order.dispute";
    public const string OrderTargetType = "order";

    /// <summary>风险复核的审计动作前缀，完整动作形如 <c>task.risk.approve</c>。</summary>
    public const string RiskReviewActionPrefix = "task.risk";

    /// <summary>一次检索最多返回多少条。</summary>
    public const int MaxLimit = 100;
    public const int DefaultLimit = 20;

    public AdminTaskListResponse SearchTasks(string? keyword, string? status, int? limit)
    {
        var normalizedLimit = Clamp(limit);
        var parsedStatus = ParseStatus(status);
        var tasks = taskQuery.Search(keyword, parsedStatus, normalizedLimit);
        var owners = userDirectory.FindMany(tasks.Select(item => item.OwnerId).Distinct().ToArray());

        return new AdminTaskListResponse(tasks.Select(item => Map(item, owners)).ToArray(), normalizedLimit);
    }

    public AdminTaskDetailResponse GetTask(Guid id)
    {
        var task = taskRepository.Get(id) ?? throw new KeyNotFoundException("任务不存在。");
        var owners = userDirectory.FindMany([task.OwnerId]);
        var workers = userDirectory.FindMany(task.Applications.Select(item => item.WorkerId).Distinct().ToArray());

        return new AdminTaskDetailResponse(
            Map(task, owners),
            task.Description,
            task.AcceptanceCriteria,
            task.Applications
                .Select(item => new AdminTaskApplicationResponse(
                    item.Id,
                    item.WorkerId,
                    workers.TryGetValue(item.WorkerId, out var worker) ? worker.DisplayName : null,
                    item.Status.ToString(),
                    item.Note,
                    item.SubmittedAt))
                .ToArray());
    }

    /// <summary>人工下架：写状态 + 记审计 + 通知相关人。已产生订单的任务会被领域规则拦住。</summary>
    public AdminTaskItemResponse CancelTask(Guid id, string reason, Guid actorId)
    {
        var task = taskRepository.Get(id) ?? throw new KeyNotFoundException("任务不存在。");
        var now = timeProvider.GetUtcNow();

        // 先让领域规则判定能不能下架（原因缺失、状态不允许都在这里拦），成立之后再写审计，避免留下“没生效的操作记录”。
        task.Cancel(reason, now);

        var applicants = task.Applications
            .Where(item => item.Status == TaskApplicationStatus.Pending)
            .Select(item => item.WorkerId)
            .ToArray();

        unitOfWork.Execute(() =>
        {
            taskRepository.Save(task);
            auditRepository.Add(AdminAuditEntry.Record(actorId, TaskCancelAction, TaskTargetType, task.Id, reason, now));

            // 下架不能悄悄进行：所有者需要知道自己的任务被处置了，报名中的服务者需要知道报名已经失效。
            notifications.EnqueueTaskCancelled(task, task.OwnerId, now);
            foreach (var applicant in applicants) notifications.EnqueueTaskCancelled(task, applicant, now);
        });

        var owners = userDirectory.FindMany([task.OwnerId]);
        return Map(task, owners);
    }

    /// <summary>运营检索订单：默认给的是待处置的争议，也可以按状态查看历史处置结果（传 all 看全部）。</summary>
    public AdminOrderListResponse SearchOrders(string? status, int? limit)
    {
        var normalizedLimit = Clamp(limit);
        var parsedStatus = ParseOrderStatus(status);
        var orders = orderQuery.Search(parsedStatus, normalizedLimit);
        var parties = userDirectory.FindMany(orders.SelectMany(item => new[] { item.OwnerId, item.WorkerId }).Distinct().ToArray());

        return new(orders.Select(item => MapOrder(item, parties)).ToArray(), normalizedLimit);
    }

    /// <summary>
    /// 处置争议：强制完成、退回返工或终止订单，必须写明依据（依据写进运营审计）。
    /// 订单状态、任务连带处理、审计与双方通知都在同一个事务里完成。
    /// </summary>
    public AdminOrderItemResponse ResolveDispute(Guid orderId, string decision, string note, Guid actorId)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        var resolution = ParseResolution(decision);
        var now = timeProvider.GetUtcNow();
        order.ResolveDispute(resolution, note, now);

        var task = taskRepository.Get(order.TaskId);
        unitOfWork.Execute(() =>
        {
            orderRepository.Save(order);

            // 任务侧的连带处理与“取消订单”一致：强制完成就关单，终止订单就放回大厅或直接过期；
            // 退回返工时任务保持 Assigned（订单还在履约中）。
            if (task is not null && task.Status == TaskStatus.Assigned)
            {
                switch (resolution)
                {
                    case DisputeResolution.Approve:
                        task.Close();
                        taskRepository.Save(task);
                        break;

                    case DisputeResolution.Cancel:
                        task.ReleaseAfterOrderCancelled(now);
                        taskRepository.Save(task);
                        break;
                }
            }

            auditRepository.Add(AdminAuditEntry.Record(actorId, $"{OrderResolveActionPrefix}.{resolution.ToString().ToLowerInvariant()}", OrderTargetType, order.Id, note, now));
            notifications.EnqueueOrderDisputeResolved(order, order.OwnerId, now);
            notifications.EnqueueOrderDisputeResolved(order, order.WorkerId, now);
        });

        var parties = userDirectory.FindMany([order.OwnerId, order.WorkerId]);
        return MapOrder(order, parties);
    }

    /// <summary>风险复核队列：等人工处置的任务，按创建时间升序（先来先处理）。</summary>
    public AdminRiskReviewListResponse ListRiskReviews(int? limit)
    {
        var normalizedLimit = Clamp(limit);
        var tasks = taskRepository.ListPendingRiskReview(normalizedLimit);
        var owners = userDirectory.FindMany(tasks.Select(item => item.OwnerId).Distinct().ToArray());

        return new(tasks.Select(item => MapRiskReview(item, owners)).ToArray(), normalizedLimit);
    }

    /// <summary>
    /// 处置风险复核：放行（之后可以发布）或驳回（不能发布，用户仍可自己撤销）。
    /// 依据必填并写进运营审计；领域层只允许处置“转人工且仍在队列里”的任务，重复处置会被拦住。
    /// </summary>
    public AdminRiskReviewItemResponse DecideRiskReview(Guid taskId, string decision, string note, Guid actorId)
    {
        var task = taskRepository.Get(taskId) ?? throw new KeyNotFoundException("任务不存在。");
        var approve = ParseRiskDecision(decision);
        var now = timeProvider.GetUtcNow();

        // 先让领域规则判定这一条能不能处置（状态不对、没写依据都在这里拦），
        // 成立之后才写审计，避免留下“没生效的操作记录”。
        if (approve) task.ApproveRiskReview(actorId, note, now);
        else task.RejectRiskReview(actorId, note, now);

        unitOfWork.Execute(() =>
        {
            taskRepository.Save(task);
            auditRepository.Add(AdminAuditEntry.Record(
                actorId, $"{RiskReviewActionPrefix}.{(approve ? "approve" : "reject")}", TaskTargetType, task.Id, note, now));
            notifications.EnqueueTaskRiskReviewed(task, task.OwnerId, now);
        });

        var owners = userDirectory.FindMany([task.OwnerId]);
        return MapRiskReview(task, owners);
    }

    public IReadOnlyCollection<AdminAuditResponse> ListAudits(int? limit) =>
        auditRepository.List(Clamp(limit))
            .Select(item => new AdminAuditResponse(item.Id, item.ActorId, item.Action, item.TargetType, item.TargetId, item.Reason, item.OccurredAt))
            .ToArray();

    public AdminUserListResponse SearchUsers(string? keyword, int? limit)
    {
        var normalizedLimit = Clamp(limit);
        var users = userDirectory.Search(keyword, normalizedLimit);
        return new AdminUserListResponse(
            users.Select(item => new AdminUserResponse(item.Id, item.Email, item.DisplayName, item.Role, item.CreatedAt)).ToArray(),
            normalizedLimit);
    }

    private static int Clamp(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private static TaskStatus? ParseStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        if (Enum.TryParse<TaskStatus>(status.Trim(), ignoreCase: true, out var parsed)) return parsed;
        throw new DomainException($"任务状态 {status} 不存在：可选值为 {string.Join("、", Enum.GetNames<TaskStatus>())}。");
    }

    private static OrderStatus? ParseOrderStatus(string? status)
    {
        // 默认只看待处置的争议；要历史记录显式传状态或 all。
        if (string.IsNullOrWhiteSpace(status)) return OrderStatus.Disputed;
        if (string.Equals(status.Trim(), "all", StringComparison.OrdinalIgnoreCase)) return null;
        if (Enum.TryParse<OrderStatus>(status.Trim(), ignoreCase: true, out var parsed)) return parsed;
        throw new DomainException($"订单状态 {status} 不存在：可选值为 {string.Join("、", Enum.GetNames<OrderStatus>())} 或 all。");
    }

    private static DisputeResolution ParseResolution(string? decision)
    {
        if (Enum.TryParse<DisputeResolution>(decision?.Trim(), ignoreCase: true, out var parsed)) return parsed;
        throw new DomainException($"争议处置结果 {decision} 不存在：可选值为 {string.Join("、", Enum.GetNames<DisputeResolution>())}。");
    }

    /// <summary>风险复核结论：Approve 放行，Reject 驳回。</summary>
    private static bool ParseRiskDecision(string? decision)
    {
        var trimmed = decision?.Trim();
        if (string.Equals(trimmed, "approve", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(trimmed, "reject", StringComparison.OrdinalIgnoreCase)) return false;
        throw new DomainException($"风险复核结论 {decision} 不存在：可选值为 Approve（放行）或 Reject（驳回）。");
    }

    private static AdminRiskReviewItemResponse MapRiskReview(TaskItem task, IReadOnlyDictionary<Guid, AdminUserView> owners)
    {
        var owner = owners.TryGetValue(task.OwnerId, out var value) ? value : null;

        return new AdminRiskReviewItemResponse(
            task.Id,
            task.Title,
            task.Description,
            task.District,
            task.Reward.Amount,
            task.Reward.Currency,
            task.Deadline,
            task.CreatedAt,
            task.OwnerId,
            owner?.DisplayName,
            owner?.Email,
            task.RiskVerdict.ToString(),
            task.RiskRuleCode,
            task.RiskCategory,
            task.RiskSummary,
            task.RiskRuleVersion,
            task.RiskAssessedAt,
            task.RiskReviewStatus.ToString(),
            task.Status.ToString(),
            task.RiskReviewedAt,
            task.RiskReviewNote,
            task.RiskReviewedBy,
            task.RiskEnforcementStatus.ToString(),
            task.RiskEnforcementReason,
            task.RiskEnforcedAt);
    }

    private static AdminOrderItemResponse MapOrder(Order order, IReadOnlyDictionary<Guid, AdminUserView> parties) => new(
        order.Id,
        order.TaskId,
        order.Title,
        order.Status.ToString(),
        order.OwnerId,
        parties.TryGetValue(order.OwnerId, out var owner) ? owner.Email : null,
        order.WorkerId,
        parties.TryGetValue(order.WorkerId, out var worker) ? worker.Email : null,
        order.Reward.Amount,
        order.Reward.Currency,
        order.CreatedAt,
        order.SubmittedAt,
        order.EvidenceNote,
        order.ReviewNote,
        order.RejectionNote,
        order.ReworkCount,
        order.CancelledAt,
        order.CancelledBy,
        order.CancellationReason,
        order.DisputeReason,
        order.DisputeOpenedBy,
        order.DisputeOpenedAt,
        order.DisputeResult,
        order.DisputeResolutionNote,
        order.DisputeResolvedAt);

    private AdminTaskItemResponse Map(TaskItem task, IReadOnlyDictionary<Guid, AdminUserView> owners)
    {
        var owner = owners.TryGetValue(task.OwnerId, out var value) ? value : null;
        var order = orderRepository.GetByTask(task.Id);

        return new AdminTaskItemResponse(
            task.Id,
            task.Title,
            task.District,
            task.Status.ToString(),
            task.Reward.Amount,
            task.Reward.Currency,
            task.Deadline,
            task.CreatedAt,
            task.OwnerId,
            owner?.DisplayName,
            owner?.Email,
            task.Applications.Count,
            task.HasExecutionAddress,
            order?.Id,
            order?.Status.ToString());
    }
}
