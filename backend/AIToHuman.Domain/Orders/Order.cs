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

/// <summary>运营对争议的处置结果。</summary>
public enum DisputeResolution
{
    /// <summary>强制完成：认定履约成立，订单直接通过并关闭任务。</summary>
    Approve,

    /// <summary>退回返工：认定还需要补做，订单回到执行中，返工次数累加。</summary>
    Rework,

    /// <summary>终止订单：认定这单不该继续，订单取消，任务回到大厅或直接过期。</summary>
    Cancel
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

    /// <summary>争议原因与处置说明的长度上限。</summary>
    public const int MaxDisputeReasonLength = 500;

    /// <summary>谁发起了争议、为什么、什么时候；以及运营处置的结果与依据。</summary>
    public string? DisputeReason { get; private set; }
    public Guid? DisputeOpenedBy { get; private set; }
    public DateTimeOffset? DisputeOpenedAt { get; private set; }

    /// <summary>处置结果（<see cref="DisputeResolution"/> 的名字）与依据。</summary>
    public string? DisputeResult { get; private set; }
    public string? DisputeResolutionNote { get; private set; }
    public DateTimeOffset? DisputeResolvedAt { get; private set; }

    public static Order Rehydrate(Guid id, Guid taskId, Guid ownerId, Guid workerId, string title, Money reward, OrderStatus status, DateTimeOffset createdAt, string? evidenceNote = null, string? reviewNote = null, DateTimeOffset? submittedAt = null, DateTimeOffset? reviewedAt = null, string? rejectionNote = null, int reworkCount = 0, DateTimeOffset? cancelledAt = null, Guid? cancelledBy = null, string? cancellationReason = null, string? disputeReason = null, Guid? disputeOpenedBy = null, DateTimeOffset? disputeOpenedAt = null, string? disputeResolution = null, string? disputeResolutionNote = null, DateTimeOffset? disputeResolvedAt = null) => new()
    {
        Id = id, TaskId = taskId, OwnerId = ownerId, WorkerId = workerId, Title = title, Reward = reward, Status = status, CreatedAt = createdAt,
        EvidenceNote = evidenceNote, ReviewNote = reviewNote, SubmittedAt = submittedAt, ReviewedAt = reviewedAt,
        RejectionNote = rejectionNote, ReworkCount = reworkCount,
        CancelledAt = cancelledAt, CancelledBy = cancelledBy, CancellationReason = cancellationReason,
        DisputeReason = disputeReason, DisputeOpenedBy = disputeOpenedBy, DisputeOpenedAt = disputeOpenedAt,
        DisputeResult = disputeResolution, DisputeResolutionNote = disputeResolutionNote, DisputeResolvedAt = disputeResolvedAt
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

    /// <summary>
    /// 平台按风控处置冻结订单：任务在发布后被复检判定为禁止类别、但已经有订单时走这条路。
    ///
    /// 它刻意复用"争议"这条既有路径：同样把订单冻结成 <see cref="OrderStatus.Disputed"/>，
    /// 于是它天然出现在运营的争议队列里，处置选项（强制完成 / 退回返工 / 终止订单）也完全一样。
    /// 区别只有一处——<see cref="DisputeOpenedBy"/> 留空表示这**不是任何一方发起的**，而是平台的动作。
    /// </summary>
    public void SuspendByRisk(string reason, DateTimeOffset now)
    {
        if (!CanBeSuspendedForRisk)
        {
            throw new DomainException($"订单当前状态 {Status} 不能按风控冻结。");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxDisputeReasonLength)
        {
            throw new DomainException($"按风控冻结订单必须写明原因，长度不超过 {MaxDisputeReasonLength} 个字符。");
        }

        Status = OrderStatus.Disputed;
        DisputeReason = trimmed;
        DisputeOpenedBy = null;
        DisputeOpenedAt = UtcTimestamp.Normalize(now);
    }

    /// <summary>还能不能被风控冻结：已结束、已取消或已经在争议里的订单不动它（争议队列里已经有人管了）。</summary>
    public bool CanBeSuspendedForRisk => Status is not (OrderStatus.Cancelled or OrderStatus.Approved or OrderStatus.Disputed);

    /// <summary>
    /// 发起争议，请平台介入。需求方只能在服务者提交验收后发起（不想验收又谈不拢时）；
    /// 服务者只能在验收被驳回后发起（不认可驳回理由、拒绝返工时）。
    /// 争议期间订单冻结：双方都不能提交、验收、驳回或取消，等运营处置。
    /// </summary>
    public void OpenDispute(Guid actorId, string? reason, DateTimeOffset now)
    {
        if (actorId != OwnerId && actorId != WorkerId) throw new DomainException("只有订单参与者可以发起争议。");

        if (actorId == OwnerId && Status != OrderStatus.Submitted)
        {
            throw new DomainException(Status == OrderStatus.Rejected
                ? "订单已经驳回，等服务者返工或由服务者发起争议。"
                : $"订单当前状态 {Status} 不能发起争议：需求方只能在服务者提交验收后发起。");
        }

        if (actorId == WorkerId && Status != OrderStatus.Rejected)
        {
            throw new DomainException(Status == OrderStatus.Submitted
                ? "服务者已经提交验收，等需求方验收或驳回；有异议请先沟通。"
                : $"订单当前状态 {Status} 不能发起争议：服务者只能在验收被驳回后发起。");
        }

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxDisputeReasonLength)
        {
            throw new DomainException($"发起争议必须写明原因，长度不超过 {MaxDisputeReasonLength} 个字符。");
        }

        Status = OrderStatus.Disputed;
        DisputeReason = trimmed;
        DisputeOpenedBy = actorId;
        DisputeOpenedAt = UtcTimestamp.Normalize(now);
    }

    /// <summary>
    /// 运营处置争议：强制完成、退回返工或终止订单，必须写明依据。
    /// 三种结果对订单的写入都发生在同一个事务里（由用例层保证），任务侧的连带处理也由用例层完成。
    /// </summary>
    public void ResolveDispute(DisputeResolution resolution, string? note, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Disputed);

        var trimmed = note?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxDisputeReasonLength)
        {
            throw new DomainException($"处置争议必须写明依据，长度不超过 {MaxDisputeReasonLength} 个字符。");
        }

        var resolvedAt = UtcTimestamp.Normalize(now);
        switch (resolution)
        {
            case DisputeResolution.Approve:
                Status = OrderStatus.Approved;
                ReviewedAt = resolvedAt;
                ReviewNote = trimmed;
                RejectionNote = null;
                break;

            case DisputeResolution.Rework:
                Status = OrderStatus.InProgress;
                ReworkCount++;
                RejectionNote = trimmed;
                break;

            case DisputeResolution.Cancel:
                Status = OrderStatus.Cancelled;
                CancelledAt = resolvedAt;
                // 终止来自平台处置而不是任何一方，因此没有取消人。
                CancelledBy = null;
                CancellationReason = trimmed;
                break;

            default:
                throw new DomainException($"未知的争议处置结果 {resolution}。");
        }

        DisputeResult = resolution.ToString();
        DisputeResolutionNote = trimmed;
        DisputeResolvedAt = resolvedAt;
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
