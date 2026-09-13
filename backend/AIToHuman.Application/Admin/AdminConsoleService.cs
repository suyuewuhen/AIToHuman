using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Application.Admin;

/// <summary>
/// 运营后台的人工兜底：跨所有者检索任务与用户、查看任务详情、把尚未分配的任务下架并留审计。
/// 风险规则引擎还没有实现，所以这里是**纯人工**介入：不做自动判定，也不假装有风险评分。
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
