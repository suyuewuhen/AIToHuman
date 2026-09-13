using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>取消订单的领域规则：谁能取消、能取消到哪一步、原因与留痕。</summary>
public sealed class OrderCancellationTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Worker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Stranger = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owner_can_cancel_before_the_worker_submits(bool startFirst)
    {
        var order = Create();
        if (startFirst) order.Start(Worker);

        order.Cancel(Owner, "  临时不需要了  ", Now);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(Now, order.CancelledAt);
        Assert.Equal(Owner, order.CancelledBy);
        // 原因去掉首尾空白后保存。
        Assert.Equal("临时不需要了", order.CancellationReason);
    }

    [Fact]
    public void Worker_can_only_cancel_before_starting()
    {
        var order = Create();
        order.Cancel(Worker, "看错距离了", Now);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(Worker, order.CancelledBy);

        var started = Create();
        started.Start(Worker);
        var error = Assert.Throws<DomainException>(() => started.Cancel(Worker, "干不了", Now));

        Assert.Contains("开始执行前", error.Message);
        Assert.Equal(OrderStatus.InProgress, started.Status);
    }

    [Fact]
    public void Submitted_orders_cannot_be_cancelled_by_either_side()
    {
        var order = Create();
        order.Start(Worker);
        order.Submit(Worker, "已完成", Now);

        var ownerError = Assert.Throws<DomainException>(() => order.Cancel(Owner, "不想验收了", Now));
        var workerError = Assert.Throws<DomainException>(() => order.Cancel(Worker, "想撤回", Now));

        Assert.Contains("先验收或驳回", ownerError.Message);
        Assert.Contains("开始执行前", workerError.Message);
        Assert.Equal(OrderStatus.Submitted, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Cancelled)]
    public void Finished_orders_cannot_be_cancelled_again(OrderStatus target)
    {
        var order = Create();
        switch (target)
        {
            case OrderStatus.Approved:
                order.Start(Worker);
                order.Submit(Worker, "已完成", Now);
                order.Approve(Owner, null, Now);
                break;
            default:
                order.Cancel(Owner, "第一次取消", Now);
                break;
        }

        Assert.Throws<DomainException>(() => order.Cancel(Owner, "再取消一次", Now));
        Assert.Equal(target, order.Status);
    }

    [Fact]
    public void Outsiders_cannot_cancel()
    {
        var order = Create();

        var error = Assert.Throws<DomainException>(() => order.Cancel(Stranger, "路过", Now));

        Assert.Contains("只有订单参与者", error.Message);
        Assert.Equal(OrderStatus.Accepted, order.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cancellation_requires_a_reason(string? reason)
    {
        var order = Create();

        var error = Assert.Throws<DomainException>(() => order.Cancel(Owner, reason, Now));

        Assert.Contains("必须填写原因", error.Message);
        Assert.Equal(OrderStatus.Accepted, order.Status);
        Assert.Null(order.CancelledAt);
    }

    [Fact]
    public void Cancellation_reason_is_bounded() =>
        Assert.Throws<DomainException>(() => Create().Cancel(Owner, new string('x', Order.MaxCancellationReasonLength + 1), Now));

    [Fact]
    public void Cancelled_orders_cannot_move_forward()
    {
        var order = Create();
        order.Cancel(Owner, "不做了", Now);

        Assert.Throws<DomainException>(() => order.Start(Worker));
        Assert.Throws<DomainException>(() => order.Submit(Worker, "完成了", Now));
        Assert.Throws<DomainException>(() => order.Approve(Owner, null, Now));
    }

    [Fact]
    public void Rehydrate_keeps_the_cancellation_trail()
    {
        var id = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var cancelledAt = Now.AddMinutes(5);

        var order = Order.Rehydrate(
            id, taskId, Owner, Worker, "代取文件", new Money(50), OrderStatus.Cancelled, Now,
            evidenceNote: null, reviewNote: null, submittedAt: null, reviewedAt: null, rejectionNote: null, reworkCount: 2,
            cancelledAt: cancelledAt, cancelledBy: Worker, cancellationReason: "临时有事");

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(cancelledAt, order.CancelledAt);
        Assert.Equal(Worker, order.CancelledBy);
        Assert.Equal("临时有事", order.CancellationReason);
        Assert.Equal(2, order.ReworkCount);
    }

    private static Order Create() => new(Guid.NewGuid(), Owner, Worker, "代取文件", new Money(50), Now);
}
