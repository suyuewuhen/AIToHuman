using AIToHuman.Domain.Common;
using AIToHuman.Domain.Payments;
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

    /// <summary>
    /// 资金托管状态与金额。刻意与订单状态分开记：订单可能"已验收"但放款失败要重试，
    /// 也可能"已取消"但退款还在路上——把两件事塞进一个字段，事后就说不清钱到底动没动。
    /// </summary>
    public EscrowStatus EscrowStatus { get; private set; } = EscrowStatus.None;

    /// <summary>被托管的金额（下单时的悬赏；托管关闭时为 0）。</summary>
    public decimal EscrowAmount { get; private set; }

    /// <summary>已经放款给服务者的金额。</summary>
    public decimal ReleasedAmount { get; private set; }

    /// <summary>已经退回需求方的金额。</summary>
    public decimal RefundedAmount { get; private set; }

    /// <summary>支付网关侧的凭据（模拟网关是确定性字符串；接真实服务商后是它返回的流水号）。</summary>
    public string? PaymentReference { get; private set; }

    public DateTimeOffset? EscrowHeldAt { get; private set; }
    public DateTimeOffset? EscrowSettledAt { get; private set; }

    /// <summary>还没动过的托管资金（可以放款或退款的余额）。</summary>
    public decimal EscrowBalance => EscrowStatus == EscrowStatus.Held ? EscrowAmount - ReleasedAmount - RefundedAmount : 0m;

    /// <summary>能不能做托管：只有从未托管过的订单可以（重复冻结是明确的错误）。</summary>
    public bool CanHoldEscrow => EscrowStatus == EscrowStatus.None;

    /// <summary>这笔托管还在等待处置（放款或退款都可以做）。</summary>
    public bool EscrowAwaitingSettlement => EscrowStatus == EscrowStatus.Held;

    /// <summary>
    /// 冻结需求方资金（下单时调用）。金额固定为订单悬赏——把"托管多少"和"订单多少钱"绑在一起，
    /// 避免出现"托管了 100 元但订单是 200 元"这种谁也说不清的状态。
    /// </summary>
    public void HoldEscrow(string paymentReference, DateTimeOffset now)
    {
        if (!CanHoldEscrow) throw new DomainException($"这笔订单的资金已经处理过（{EscrowStatus}），不能重复托管。");
        if (Reward.Amount <= 0) throw new DomainException("订单金额必须大于 0 才能托管。");
        if (string.IsNullOrWhiteSpace(paymentReference)) throw new DomainException("托管必须记录支付凭据。");

        EscrowStatus = EscrowStatus.Held;
        EscrowAmount = Reward.Amount;
        ReleasedAmount = 0m;
        RefundedAmount = 0m;
        PaymentReference = paymentReference.Trim();
        EscrowHeldAt = UtcTimestamp.Normalize(now);
        EscrowSettledAt = null;
    }

    /// <summary>验收通过：托管全额放款给服务者。</summary>
    public void ReleaseEscrow(DateTimeOffset now) => Settle(EscrowAmount, 0m, now);

    /// <summary>订单取消：托管全额退回需求方。</summary>
    public void RefundEscrow(DateTimeOffset now) => Settle(0m, EscrowAmount, now);

    /// <summary>
    /// 争议处置后的分账：<paramref name="workerAmount"/> 放款给服务者、<paramref name="ownerAmount"/> 退回需求方，
    /// 两者之和必须正好等于托管金额（不允许有"不知道去哪了"的差额）。
    /// </summary>
    public void SettleEscrow(decimal workerAmount, decimal ownerAmount, DateTimeOffset now) => Settle(workerAmount, ownerAmount, now);

    private void Settle(decimal workerAmount, decimal ownerAmount, DateTimeOffset now)
    {
        if (EscrowStatus != EscrowStatus.Held)
        {
            throw new DomainException(EscrowStatus == EscrowStatus.None
                ? "这笔订单没有托管资金，无法放款或退款。"
                : $"这笔订单的托管资金已经处理过（{EscrowStatus}），不能重复处置。");
        }

        if (workerAmount < 0 || ownerAmount < 0) throw new DomainException("放款与退款的金额不能为负。");
        if (workerAmount == 0 && ownerAmount == 0) throw new DomainException("放款与退款至少要有一项大于 0。");
        if (workerAmount + ownerAmount != EscrowAmount)
        {
            throw new DomainException($"放款与退款金额之和必须等于托管金额（{EscrowAmount:0.##}），当前为 {workerAmount + ownerAmount:0.##}。");
        }

        ReleasedAmount = workerAmount;
        RefundedAmount = ownerAmount;
        EscrowSettledAt = UtcTimestamp.Normalize(now);
        EscrowStatus = ownerAmount == 0m
            ? EscrowStatus.Released
            : workerAmount == 0m ? EscrowStatus.Refunded : EscrowStatus.Settled;
    }

    public static Order Rehydrate(Guid id, Guid taskId, Guid ownerId, Guid workerId, string title, Money reward, OrderStatus status, DateTimeOffset createdAt, string? evidenceNote = null, string? reviewNote = null, DateTimeOffset? submittedAt = null, DateTimeOffset? reviewedAt = null, string? rejectionNote = null, int reworkCount = 0, DateTimeOffset? cancelledAt = null, Guid? cancelledBy = null, string? cancellationReason = null, string? disputeReason = null, Guid? disputeOpenedBy = null, DateTimeOffset? disputeOpenedAt = null, string? disputeResolution = null, string? disputeResolutionNote = null, DateTimeOffset? disputeResolvedAt = null, EscrowStatus escrowStatus = EscrowStatus.None, decimal escrowAmount = 0m, decimal releasedAmount = 0m, decimal refundedAmount = 0m, string? paymentReference = null, DateTimeOffset? escrowHeldAt = null, DateTimeOffset? escrowSettledAt = null) => new()
    {
        Id = id, TaskId = taskId, OwnerId = ownerId, WorkerId = workerId, Title = title, Reward = reward, Status = status, CreatedAt = createdAt,
        EvidenceNote = evidenceNote, ReviewNote = reviewNote, SubmittedAt = submittedAt, ReviewedAt = reviewedAt,
        RejectionNote = rejectionNote, ReworkCount = reworkCount,
        CancelledAt = cancelledAt, CancelledBy = cancelledBy, CancellationReason = cancellationReason,
        DisputeReason = disputeReason, DisputeOpenedBy = disputeOpenedBy, DisputeOpenedAt = disputeOpenedAt,
        DisputeResult = disputeResolution, DisputeResolutionNote = disputeResolutionNote, DisputeResolvedAt = disputeResolvedAt,
        EscrowStatus = escrowStatus, EscrowAmount = escrowAmount, ReleasedAmount = releasedAmount, RefundedAmount = refundedAmount,
        PaymentReference = paymentReference, EscrowHeldAt = escrowHeldAt, EscrowSettledAt = escrowSettledAt
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
