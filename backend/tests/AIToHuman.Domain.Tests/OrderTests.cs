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
}
