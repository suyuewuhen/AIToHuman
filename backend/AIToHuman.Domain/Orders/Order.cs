using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Orders;

public enum OrderStatus
{
    Accepted,
    InProgress,
    Submitted,
    Approved,
    Rejected,
    Disputed,
    Cancelled
}

public sealed class Order
{
    private Order() { }

    public Order(Guid taskId, Guid ownerId, Guid workerId, string title, Money reward, DateTimeOffset createdAt)
    {
        if (taskId == Guid.Empty || ownerId == Guid.Empty || workerId == Guid.Empty) throw new DomainException("订单必须关联有效的任务和参与者。");
        if (ownerId == workerId) throw new DomainException("任务发布者不能成为自己的服务者。");
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("订单必须包含标题。");
        Id = Guid.NewGuid();
        TaskId = taskId;
        OwnerId = ownerId;
        WorkerId = workerId;
        Title = title.Trim();
        Reward = reward;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid WorkerId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public Money Reward { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.Accepted;
    public DateTimeOffset CreatedAt { get; private set; }
    public string? EvidenceNote { get; private set; }
    public string? ReviewNote { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public string? RejectionNote { get; private set; }
    public int ReworkCount { get; private set; }

    /// <summary>取消时间、发起人与原因。原因必填，便于对方与运营事后追溯谁在什么时候终止了订单。</summary>
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public string? CancellationReason { get; private set; }

    /// <summary>取消原因的长度上限。</summary>
    public const int MaxCancellationReasonLength = 200;

    public static Order Rehydrate(Guid id, Guid taskId, Guid ownerId, Guid workerId, string title, Money reward, OrderStatus status, DateTimeOffset createdAt, string? evidenceNote = null, string? reviewNote = null, DateTimeOffset? submittedAt = null, DateTimeOffset? reviewedAt = null, string? rejectionNote = null, int reworkCount = 0, DateTimeOffset? cancelledAt = null, Guid? cancelledBy = null, string? cancellationReason = null) => new()
    {
        Id = id, TaskId = taskId, OwnerId = ownerId, WorkerId = workerId, Title = title, Reward = reward, Status = status, CreatedAt = createdAt,
        EvidenceNote = evidenceNote, ReviewNote = reviewNote, SubmittedAt = submittedAt, ReviewedAt = reviewedAt,
        RejectionNote = rejectionNote, ReworkCount = reworkCount,
        CancelledAt = cancelledAt, CancelledBy = cancelledBy, CancellationReason = cancellationReason
    };

    public void Start(Guid actorId)
    {
        EnsureWorker(actorId);
        EnsureStatus(OrderStatus.Accepted);
        Status = OrderStatus.InProgress;
    }

    public void Submit(Guid actorId, string? evidenceNote, DateTimeOffset now)
    {
        EnsureWorker(actorId);
        EnsureStatus(OrderStatus.InProgress);
        if (string.IsNullOrWhiteSpace(evidenceNote)) throw new DomainException("提交验收必须填写执行凭证或完成说明。");
        EvidenceNote = evidenceNote.Trim();
        SubmittedAt = UtcTimestamp.Normalize(now);
        Status = OrderStatus.Submitted;
    }

    public void Approve(Guid actorId, string? reviewNote, DateTimeOffset now)
    {
        EnsureOwner(actorId);
        EnsureStatus(OrderStatus.Submitted);
        ReviewNote = string.IsNullOrWhiteSpace(reviewNote) ? "验收通过" : reviewNote.Trim();
        ReviewedAt = UtcTimestamp.Normalize(now);
        // 验收通过后驳回原因已处理完毕；返工次数作为履约历史保留。
        RejectionNote = null;
        Status = OrderStatus.Approved;
    }

    public void Reject(Guid actorId, string? reviewNote, DateTimeOffset now)
    {
        EnsureOwner(actorId);
        EnsureStatus(OrderStatus.Submitted);
        if (string.IsNullOrWhiteSpace(reviewNote)) throw new DomainException("驳回验收必须填写补充说明。");
        ReviewNote = reviewNote.Trim();
        RejectionNote = reviewNote.Trim();
        ReviewedAt = UtcTimestamp.Normalize(now);
        Status = OrderStatus.Rejected;
    }

    /// <summary>服务者按需求方的驳回说明重新开始履约。返工次数累加，驳回原因保留以便追溯。</summary>
    public void ResumeRework(Guid actorId)
    {
        EnsureWorker(actorId);
        EnsureStatus(OrderStatus.Rejected);
        ReworkCount++;
        Status = OrderStatus.InProgress;
    }

    /// <summary>
    /// 取消订单。需求方可以在服务者提交验收之前取消；服务者只在还没开始执行（<see cref="OrderStatus.Accepted"/>）时
    /// 才能单方面取消，一旦开工就要由需求方发起终止，避免“接了单又甩单”无人负责。
    /// 提交验收后（<see cref="OrderStatus.Submitted"/>）双方都不能取消：先验收或驳回，争议走后续流程。
    /// 取消必须填写原因，原因与取消人一并留痕。
    /// </summary>
    public void Cancel(Guid actorId, string? reason, DateTimeOffset now)
    {
        if (actorId != OwnerId && actorId != WorkerId) throw new DomainException("只有订单参与者可以取消订单。");

        if (actorId == WorkerId && Status != OrderStatus.Accepted)
        {
            throw new DomainException($"服务者只能在开始执行前取消订单，当前状态 {Status} 请与需求方协商后由需求方处理。");
        }

        if (actorId == OwnerId && Status is not (OrderStatus.Accepted or OrderStatus.InProgress))
        {
            throw Status == OrderStatus.Submitted
                ? new DomainException("服务者已经提交验收，请先验收或驳回，不要直接取消。")
                : new DomainException($"订单当前状态 {Status} 不允许取消。");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxCancellationReasonLength)
        {
            throw new DomainException($"取消订单必须填写原因，长度不超过 {MaxCancellationReasonLength} 个字符。");
        }

        Status = OrderStatus.Cancelled;
        CancelledAt = UtcTimestamp.Normalize(now);
        CancelledBy = actorId;
        CancellationReason = trimmed;
    }

    private void EnsureWorker(Guid actorId)
    {
        if (actorId != WorkerId) throw new DomainException("只有订单服务者可以执行该操作。");
    }

    private void EnsureOwner(Guid actorId)
    {
        if (actorId != OwnerId) throw new DomainException("只有需求方可以执行该操作。");
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (Status != expected) throw new DomainException($"订单当前状态 {Status} 不允许该操作，期望状态为 {expected}。");
    }
}
