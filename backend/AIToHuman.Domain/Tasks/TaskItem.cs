using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Tasks;

public sealed class TaskItem
{
    private readonly List<TaskApplication> _applications = [];

    private TaskItem()
    {
        Title = string.Empty;
        Description = string.Empty;
        District = string.Empty;
        AcceptanceCriteria = [];
    }

    public TaskItem(
        Guid ownerId,
        string title,
        string description,
        string district,
        DateTimeOffset deadline,
        Money reward,
        IEnumerable<string> acceptanceCriteria,
        DateTimeOffset createdAt,
        string? executionAddress = null)
    {
        var criteria = acceptanceCriteria
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct()
            .ToArray();

        if (ownerId == Guid.Empty) throw new DomainException("任务必须有所有者。");
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 80) throw new DomainException("任务标题必须为 1 到 80 个字符。");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("任务描述不能为空。");
        if (string.IsNullOrWhiteSpace(district)) throw new DomainException("任务必须包含公开区域。");
        if (UtcTimestamp.Normalize(deadline) <= UtcTimestamp.Normalize(createdAt)) throw new DomainException("截止时间必须晚于创建时间。");
        if (criteria.Length == 0) throw new DomainException("任务至少需要一项验收标准。");
        if (executionAddress?.Trim().Length > MaxExecutionAddressLength) throw new DomainException($"执行地址不能超过 {MaxExecutionAddressLength} 个字符。");

        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Title = title.Trim();
        Description = description.Trim();
        District = district.Trim();
        Deadline = UtcTimestamp.Normalize(deadline);
        Reward = reward;
        AcceptanceCriteria = criteria;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
        ExecutionAddress = NormalizeExecutionAddress(executionAddress);
    }

    /// <summary>执行地址属于订单参与者层信息，最长 200 字。</summary>
    public const int MaxExecutionAddressLength = 200;

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public string District { get; private set; }

    /// <summary>精确执行地址，只在订单成立后向参与者披露；大厅与公开详情永远不含它。</summary>
    public string? ExecutionAddress { get; private set; }

    public DateTimeOffset Deadline { get; private set; }
    public Money Reward { get; private set; }
    public IReadOnlyList<string> AcceptanceCriteria { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public TaskStatus Status { get; private set; } = TaskStatus.ReadyToPublish;
    public IReadOnlyCollection<TaskApplication> Applications => _applications.AsReadOnly();

    public bool HasExecutionAddress => !string.IsNullOrWhiteSpace(ExecutionAddress);

    public static TaskItem Rehydrate(
        Guid id,
        Guid ownerId,
        string title,
        string description,
        string district,
        DateTimeOffset deadline,
        Money reward,
        IReadOnlyList<string> acceptanceCriteria,
        DateTimeOffset createdAt,
        TaskStatus status,
        IEnumerable<TaskApplication> applications,
        string? executionAddress = null)
    {
        var task = new TaskItem
        {
            Id = id,
            OwnerId = ownerId,
            Title = title,
            Description = description,
            District = district,
            Deadline = deadline,
            Reward = reward,
            AcceptanceCriteria = acceptanceCriteria,
            CreatedAt = createdAt,
            Status = status,
            ExecutionAddress = NormalizeExecutionAddress(executionAddress)
        };
        task._applications.AddRange(applications);
        return task;
    }

    /// <summary>
    /// 执行地址的分阶段披露：所有者始终可见；被选中的服务者在订单成立后可见；
    /// 其他任何人（包括已报名但未被选中的服务者）都拿不到。
    /// </summary>
    public string? ExecutionAddressFor(Guid? viewerId)
    {
        if (viewerId is null || viewerId == Guid.Empty) return null;
        if (viewerId == OwnerId) return ExecutionAddress;

        var selected = _applications.FirstOrDefault(item => item.Status == TaskApplicationStatus.Selected);
        return selected is not null && selected.WorkerId == viewerId ? ExecutionAddress : null;
    }

    private static string? NormalizeExecutionAddress(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public void Publish(DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.ReadyToPublish);
        if (Deadline <= now) throw new DomainException("已过截止时间的任务不能发布。");
        Status = TaskStatus.Published;
    }

    public void IncreaseReward(Money reward)
    {
        EnsureStatus(TaskStatus.Published);
        if (reward.Currency != Reward.Currency || reward.Amount <= Reward.Amount)
        {
            throw new DomainException("已发布任务只能提高同币种悬赏。");
        }

        Reward = reward;
    }

    public TaskApplication Apply(Guid workerId, string? note, DateTimeOffset now)
    {
        EnsureStatus(TaskStatus.Published);
        if (workerId == OwnerId) throw new DomainException("任务发布者不能报名自己的任务。");
        if (_applications.Any(item => item.WorkerId == workerId && item.Status == TaskApplicationStatus.Pending))
        {
            throw new DomainException("服务者已经报名该任务。");
        }

        var application = new TaskApplication(workerId, note ?? string.Empty, now);
        _applications.Add(application);
        return application;
    }

    public TaskApplication SelectApplication(Guid applicationId)
    {
        EnsureStatus(TaskStatus.Published);
        var selected = _applications.SingleOrDefault(item => item.Id == applicationId)
            ?? throw new DomainException("报名不存在。");
        if (selected.Status != TaskApplicationStatus.Pending) throw new DomainException("只能选择有效报名。");

        foreach (var application in _applications.Where(item => item.Status == TaskApplicationStatus.Pending))
        {
            application.Status = application.Id == applicationId
                ? TaskApplicationStatus.Selected
                : TaskApplicationStatus.Rejected;
        }

        Status = TaskStatus.Assigned;
        return selected;
    }

    /// <summary>订单验收通过后关闭任务。只有已分配的任务可以关闭，关闭后不再出现在任务大厅。</summary>
    public void Close()
    {
        EnsureStatus(TaskStatus.Assigned);
        Status = TaskStatus.Closed;
    }

    /// <summary>
    /// 运营人工下架：草稿或已发布但尚未分配的任务可以下架，下架后不再出现在任务大厅。
    /// 必须给出原因（原因本身记在运营审计里，任务实体只负责拦住“没有原因的下架”）。
    /// 已经产生订单的任务不能直接下架——订单要先按正常流程结束或走争议处理，
    /// 否则会出现“任务消失了但订单还挂在服务者名下”的状态。
    /// </summary>
    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status is not (TaskStatus.ReadyToPublish or TaskStatus.Published))
        {
            throw Status == TaskStatus.Assigned
                ? new DomainException("任务已经分配并产生订单，不能直接下架：请先处理订单（验收、驳回或走争议流程）。")
                : new DomainException($"任务当前状态 {Status} 不允许下架。");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxCancellationReasonLength)
            throw new DomainException($"下架任务必须填写原因，长度不超过 {MaxCancellationReasonLength} 个字符。");

        _ = UtcTimestamp.Normalize(now);
        Status = TaskStatus.Cancelled;
    }

    /// <summary>下架原因的长度上限（原因记在运营审计表里）。</summary>
    public const int MaxCancellationReasonLength = 200;

    private void EnsureStatus(TaskStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"任务当前状态 {Status} 不允许该操作，期望状态为 {expected}。");
        }
    }
}
