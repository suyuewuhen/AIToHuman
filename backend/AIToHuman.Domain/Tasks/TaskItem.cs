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
        DateTimeOffset createdAt)
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
        if (deadline <= createdAt) throw new DomainException("截止时间必须晚于创建时间。");
        if (criteria.Length == 0) throw new DomainException("任务至少需要一项验收标准。");

        Id = Guid.NewGuid();
        OwnerId = ownerId;
        Title = title.Trim();
        Description = description.Trim();
        District = district.Trim();
        Deadline = deadline;
        Reward = reward;
        AcceptanceCriteria = criteria;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public string Title { get; private set; }
    public string Description { get; private set; }
    public string District { get; private set; }
    public DateTimeOffset Deadline { get; private set; }
    public Money Reward { get; private set; }
    public IReadOnlyList<string> AcceptanceCriteria { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public TaskStatus Status { get; private set; } = TaskStatus.ReadyToPublish;
    public IReadOnlyCollection<TaskApplication> Applications => _applications.AsReadOnly();

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
        IEnumerable<TaskApplication> applications)
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
            Status = status
        };
        task._applications.AddRange(applications);
        return task;
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

    private void EnsureStatus(TaskStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"任务当前状态 {Status} 不允许该操作，期望状态为 {expected}。");
        }
    }
}
