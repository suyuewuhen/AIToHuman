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
        CreatedAt = createdAt;
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

    public static Order Rehydrate(Guid id, Guid taskId, Guid ownerId, Guid workerId, string title, Money reward, OrderStatus status, DateTimeOffset createdAt, string? evidenceNote = null, string? reviewNote = null, DateTimeOffset? submittedAt = null, DateTimeOffset? reviewedAt = null) => new()
    {
        Id = id, TaskId = taskId, OwnerId = ownerId, WorkerId = workerId, Title = title, Reward = reward, Status = status, CreatedAt = createdAt,
        EvidenceNote = evidenceNote, ReviewNote = reviewNote, SubmittedAt = submittedAt, ReviewedAt = reviewedAt
    };

    public void Start(Guid actorId)
    {
        EnsureParticipant(actorId, WorkerId);
        EnsureStatus(OrderStatus.Accepted);
        Status = OrderStatus.InProgress;
    }

    public void Submit(Guid actorId, string? evidenceNote, DateTimeOffset now)
    {
        EnsureParticipant(actorId, WorkerId);
        EnsureStatus(OrderStatus.InProgress);
        if (string.IsNullOrWhiteSpace(evidenceNote)) throw new DomainException("提交验收必须填写执行凭证或完成说明。");
        EvidenceNote = evidenceNote.Trim();
        SubmittedAt = now;
        Status = OrderStatus.Submitted;
    }

    public void Approve(Guid actorId, string? reviewNote, DateTimeOffset now)
    {
        EnsureParticipant(actorId, OwnerId);
        EnsureStatus(OrderStatus.Submitted);
        ReviewNote = string.IsNullOrWhiteSpace(reviewNote) ? "验收通过" : reviewNote.Trim();
        ReviewedAt = now;
        Status = OrderStatus.Approved;
    }

    public void Reject(Guid actorId, string? reviewNote, DateTimeOffset now)
    {
        EnsureParticipant(actorId, OwnerId);
        EnsureStatus(OrderStatus.Submitted);
        if (string.IsNullOrWhiteSpace(reviewNote)) throw new DomainException("驳回验收必须填写补充说明。");
        ReviewNote = reviewNote.Trim();
        ReviewedAt = now;
        Status = OrderStatus.Rejected;
    }

    private void EnsureParticipant(Guid actorId, Guid expectedActor)
    {
        if (actorId != expectedActor) throw new DomainException("当前用户不是该操作的订单参与者。");
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (Status != expected) throw new DomainException($"订单当前状态 {Status} 不允许该操作，期望状态为 {expected}。");
    }
}
