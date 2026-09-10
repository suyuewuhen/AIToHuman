using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Tests;

public sealed class OrderTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Order_follows_worker_execution_and_owner_approval_flow()
    {
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), owner, worker, "代取文件", new Money(50), _now);

        order.Start(worker);
        order.Submit(worker, "已拍照并交付", _now.AddHours(1));
        order.Approve(owner, "验收通过", _now.AddHours(2));

        Assert.Equal(OrderStatus.Approved, order.Status);
    }

    [Fact]
    public void Order_rejects_wrong_actor_and_invalid_transition()
    {
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), owner, worker, "代取文件", new Money(50), _now);

        Assert.Throws<DomainException>(() => order.Start(owner));
        order.Start(worker);
        Assert.Throws<DomainException>(() => order.Approve(owner, "过早验收", _now.AddHours(1)));
        order.Submit(worker, "已拍照并交付", _now.AddHours(1));
        Assert.Throws<DomainException>(() => order.Submit(worker, "重复提交", _now.AddHours(2)));
    }

    [Fact]
    public void Rejected_order_can_resume_rework_and_be_approved_after_resubmission()
    {
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), owner, worker, "代取文件", new Money(50), _now);

        order.Start(worker);
        order.Submit(worker, "第一次提交", _now.AddHours(1));
        order.Reject(owner, "照片没有拍到取件码", _now.AddHours(2));
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal("照片没有拍到取件码", order.RejectionNote);
        Assert.Equal(0, order.ReworkCount);

        order.ResumeRework(worker);
        Assert.Equal(OrderStatus.InProgress, order.Status);
        Assert.Equal(1, order.ReworkCount);
        Assert.Equal("照片没有拍到取件码", order.RejectionNote);

        order.Submit(worker, "已补拍取件码", _now.AddHours(3));
        order.Approve(owner, "验收通过", _now.AddHours(4));

        Assert.Equal(OrderStatus.Approved, order.Status);
        Assert.Equal(1, order.ReworkCount);
        Assert.Null(order.RejectionNote);
    }

    [Fact]
    public void Only_worker_can_resume_rework_and_only_from_rejected_status()
    {
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), owner, worker, "代取文件", new Money(50), _now);

        Assert.Throws<DomainException>(() => order.ResumeRework(worker));
        order.Start(worker);
        order.Submit(worker, "第一次提交", _now.AddHours(1));
        order.Reject(owner, "照片模糊", _now.AddHours(2));

        Assert.Throws<DomainException>(() => order.ResumeRework(owner));
        order.ResumeRework(worker);
        Assert.Throws<DomainException>(() => order.ResumeRework(worker));
        Assert.Equal(1, order.ReworkCount);
    }

    [Fact]
    public void Guard_messages_distinguish_worker_and_owner_actions()
    {
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = new Order(Guid.NewGuid(), owner, worker, "代取文件", new Money(50), _now);

        Assert.Equal("只有订单服务者可以执行该操作。", Assert.Throws<DomainException>(() => order.Start(owner)).Message);

        order.Start(worker);
        order.Submit(worker, "第一次提交", _now.AddHours(1));
        Assert.Equal("只有需求方可以执行该操作。", Assert.Throws<DomainException>(() => order.Approve(worker, "越权验收", _now.AddHours(2))).Message);

        order.Reject(owner, "照片模糊", _now.AddHours(2));
        Assert.Equal("只有订单服务者可以执行该操作。", Assert.Throws<DomainException>(() => order.ResumeRework(owner)).Message);
    }
}
