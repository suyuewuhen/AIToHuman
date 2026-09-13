using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Tests;

/// <summary>争议的领域规则：谁能发起、什么阶段能发起、运营三种处置结果分别怎么改订单。</summary>
public sealed class DisputeTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Worker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Stranger = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_can_open_a_dispute_after_the_worker_submitted()
    {
        var order = Submitted();

        order.OpenDispute(Owner, "  交付内容与验收标准不符  ", Now.AddHours(1));

        Assert.Equal(OrderStatus.Disputed, order.Status);
        Assert.Equal("交付内容与验收标准不符", order.DisputeReason);
        Assert.Equal(Owner, order.DisputeOpenedBy);
        Assert.Equal(Now.AddHours(1), order.DisputeOpenedAt);
    }

    [Fact]
    public void Worker_can_open_a_dispute_after_being_rejected()
    {
        var order = Rejected();

        order.OpenDispute(Worker, "驳回理由不成立，我已经按要求做了", Now.AddHours(1));

        Assert.Equal(OrderStatus.Disputed, order.Status);
        Assert.Equal(Worker, order.DisputeOpenedBy);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.InProgress)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Cancelled)]
    public void Owner_cannot_open_a_dispute_outside_submitted(OrderStatus status)
    {
        var order = InStatus(status);

        Assert.Throws<DomainException>(() => order.OpenDispute(Owner, "有异议", Now.AddHours(2)));
        Assert.Equal(status, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Accepted)]
    [InlineData(OrderStatus.InProgress)]
    [InlineData(OrderStatus.Submitted)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Cancelled)]
    public void Worker_cannot_open_a_dispute_outside_rejected(OrderStatus status)
    {
        var order = InStatus(status);

        Assert.Throws<DomainException>(() => order.OpenDispute(Worker, "有异议", Now.AddHours(2)));
        Assert.Equal(status, order.Status);
    }

    [Fact]
    public void Outsiders_cannot_open_a_dispute()
    {
        var order = Submitted();

        var error = Assert.Throws<DomainException>(() => order.OpenDispute(Stranger, "路过", Now));

        Assert.Contains("只有订单参与者", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Opening_a_dispute_requires_a_reason(string? reason)
    {
        var order = Submitted();

        var error = Assert.Throws<DomainException>(() => order.OpenDispute(Owner, reason, Now));

        Assert.Contains("必须写明原因", error.Message);
        Assert.Equal(OrderStatus.Submitted, order.Status);
    }

    [Fact]
    public void Dispute_reason_and_resolution_note_are_bounded()
    {
        var order = Submitted();
        Assert.Throws<DomainException>(() => order.OpenDispute(Owner, new string('x', Order.MaxDisputeReasonLength + 1), Now));

        var disputed = Submitted();
        disputed.OpenDispute(Owner, "有异议", Now);
        Assert.Throws<DomainException>(() => disputed.ResolveDispute(DisputeResolution.Approve, new string('x', Order.MaxDisputeReasonLength + 1), Now));
    }

    [Fact]
    public void A_disputed_order_is_frozen_until_the_platform_resolves_it()
    {
        var order = Submitted();
        order.OpenDispute(Owner, "有异议", Now);

        // 争议期间双方都动不了：提交、验收、驳回、返工、取消全部拒绝。
        Assert.Throws<DomainException>(() => order.Submit(Worker, "再交一次", Now));
        Assert.Throws<DomainException>(() => order.Approve(Owner, null, Now));
        Assert.Throws<DomainException>(() => order.Reject(Owner, "不行", Now));
        Assert.Throws<DomainException>(() => order.ResumeRework(Worker));
        Assert.Throws<DomainException>(() => order.Cancel(Owner, "算了", Now));
        Assert.Equal(OrderStatus.Disputed, order.Status);
    }

    [Fact]
    public void Approving_resolves_the_dispute_by_completing_the_order()
    {
        var order = Submitted();
        order.OpenDispute(Owner, "有异议", Now);

        order.ResolveDispute(DisputeResolution.Approve, "凭证符合验收标准，判定完成", Now.AddHours(3));

        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.Equal(Now.AddHours(3), order.ReviewedAt);
        Assert.Equal("凭证符合验收标准，判定完成", order.ReviewNote);
        Assert.Null(order.RejectionNote);
        Assert.Equal("Approve", order.DisputeResult);
        Assert.Equal("凭证符合验收标准，判定完成", order.DisputeResolutionNote);
        Assert.Equal(Now.AddHours(3), order.DisputeResolvedAt);
    }

    [Fact]
    public void Rework_sends_the_order_back_to_execution_and_counts_the_round()
    {
        var order = Submitted();
        order.OpenDispute(Owner, "有异议", Now);

        order.ResolveDispute(DisputeResolution.Rework, "缺少取件码照片，请补交", Now.AddHours(3));

        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(1, order.ReworkCount);
        Assert.Equal("缺少取件码照片，请补交", order.RejectionNote);
        Assert.Equal("Rework", order.DisputeResult);
    }

    [Fact]
    public void Cancelling_resolves_the_dispute_by_ending_the_order_without_a_participant_canceller()
    {
        var order = Submitted();
        order.OpenDispute(Owner, "有异议", Now);

        order.ResolveDispute(DisputeResolution.Cancel, "证据不足以判定，终止订单", Now.AddHours(3));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(Now.AddHours(3), order.CancelledAt);
        // 终止来自平台处置，不是任何一方取消。
        Assert.Null(order.CancelledBy);
        Assert.Equal("证据不足以判定，终止订单", order.CancellationReason);
        Assert.Equal("Cancel", order.DisputeResult);
    }

    [Fact]
    public void Only_disputed_orders_can_be_resolved_and_a_note_is_required()
    {
        var running = InStatus(OrderStatus.InProgress);
        Assert.Throws<DomainException>(() => running.ResolveDispute(DisputeResolution.Approve, "依据", Now));

        var disputed = Submitted();
        disputed.OpenDispute(Owner, "有异议", Now);
        Assert.Throws<DomainException>(() => disputed.ResolveDispute(DisputeResolution.Approve, "  ", Now));
        Assert.Equal(OrderStatus.Disputed, disputed.Status);

        disputed.ResolveDispute(DisputeResolution.Approve, "依据", Now);
        // 处置过一次就不能再处置。
        Assert.Throws<DomainException>(() => disputed.ResolveDispute(DisputeResolution.Cancel, "再来一次", Now));
    }

    [Fact]
    public void Rehydrate_keeps_the_dispute_trail()
    {
        var order = Order.Rehydrate(
            Guid.NewGuid(), Guid.NewGuid(), Owner, Worker, "代取文件", new Money(50), OrderStatus.Disputed, Now,
            disputeReason: "交付不符", disputeOpenedBy: Owner, disputeOpenedAt: Now.AddHours(1),
            disputeResolution: "Rework", disputeResolutionNote: "请补交照片", disputeResolvedAt: Now.AddHours(2));

        Assert.Equal(OrderStatus.Disputed, order.Status);
        Assert.Equal("交付不符", order.DisputeReason);
        Assert.Equal(Owner, order.DisputeOpenedBy);
        Assert.Equal("Rework", order.DisputeResult);
        Assert.Equal("请补交照片", order.DisputeResolutionNote);
        Assert.Equal(Now.AddHours(2), order.DisputeResolvedAt);
    }

    private static Order Submitted()
    {
        var order = Create();
        order.Start(Worker);
        order.Submit(Worker, "已完成", Now);
        return order;
    }

    private static Order Rejected()
    {
        var order = Submitted();
        order.Reject(Owner, "照片看不清", Now);
        return order;
    }

    private static Order Create() => new(Guid.NewGuid(), Owner, Worker, "代取文件", new Money(50), Now);

    private static Order InStatus(OrderStatus status)
    {
        switch (status)
        {
            case OrderStatus.Accepted:
                return Create();
            case OrderStatus.InProgress:
                var running = Create();
                running.Start(Worker);
                return running;
            case OrderStatus.Submitted:
                return Submitted();
            case OrderStatus.Rejected:
                return Rejected();
            case OrderStatus.Disputed:
                var disputed = Submitted();
                disputed.OpenDispute(Owner, "有异议", Now);
                return disputed;
            case OrderStatus.Approved:
                var approved = Submitted();
                approved.Approve(Owner, null, Now);
                return approved;
            default:
                var cancelled = Create();
                cancelled.Cancel(Owner, "不做了", Now);
                return cancelled;
        }
    }
}
