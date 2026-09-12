using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
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
    IUserDirectory userDirectory,
    ITaskRepository taskRepository,
    IOrderRepository orderRepository,
    IAdminAuditRepository auditRepository,
    TimeProvider timeProvider)
{
    /// <summary>运营下架动作的审计名称。</summary>
    public const string TaskCancelAction = "task.cancel";
    public const string TaskTargetType = "task";

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

    /// <summary>人工下架：写状态 + 记审计。已产生订单的任务会被领域规则拦住。</summary>
    public AdminTaskItemResponse CancelTask(Guid id, string reason, Guid actorId)
    {
        var task = taskRepository.Get(id) ?? throw new KeyNotFoundException("任务不存在。");
        var now = timeProvider.GetUtcNow();

        // 先让领域规则判定能不能下架（原因缺失、状态不允许都在这里拦），成立之后再写审计，避免留下“没生效的操作记录”。
        task.Cancel(reason, now);
        taskRepository.Save(task);

        auditRepository.Add(AdminAuditEntry.Record(actorId, TaskCancelAction, TaskTargetType, task.Id, reason, now));

        var owners = userDirectory.FindMany([task.OwnerId]);
        return Map(task, owners);
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
