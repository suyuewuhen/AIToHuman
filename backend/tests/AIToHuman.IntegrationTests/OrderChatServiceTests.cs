using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;

namespace AIToHuman.IntegrationTests;

/// <summary>订单会话：参与者权限、消息顺序、未读语义，以及与通知的同事务写入。</summary>
public sealed class OrderChatServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Participants_exchange_messages_in_chronological_order()
    {
        var (service, orders, _, _) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = AddOrder(orders, owner, worker);

        service.Send(order.Id, owner, "  请下午三点前送到  ");
        service.Send(order.Id, worker, "好的，两点半到");

        var view = service.List(order.Id, owner, 50);

        Assert.Equal(2, view.Items.Count);
        Assert.Equal("请下午三点前送到", view.Items[0].Content);
        Assert.Equal(owner, view.Items[0].SenderId);
        Assert.Equal("好的，两点半到", view.Items[1].Content);
        // 需求方自己的消息不算未读，服务者那条算一条。
        Assert.Equal(1, view.UnreadCount);
    }

    [Fact]
    public void Sending_notifies_the_other_participant()
    {
        var (service, orders, notifications, _) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = AddOrder(orders, owner, worker);

        service.Send(order.Id, owner, new string('好', 80));

        var notification = Assert.Single(notifications.ListByUser(worker, 10));
        Assert.Equal(NotificationTypes.OrderMessageCreated, notification.Type);
        var payload = service.List(order.Id, worker, 50);
        Assert.Equal(1, payload.UnreadCount);
    }

    [Fact]
    public void Same_message_id_is_not_notified_twice()
    {
        var (service, orders, notifications, _) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = AddOrder(orders, owner, worker);

        service.Send(order.Id, owner, "第一条");
        service.Send(order.Id, owner, "第二条");

        Assert.Equal(2, notifications.ListByUser(worker, 10).Count);
    }

    [Fact]
    public void Non_participants_are_rejected()
    {
        var (service, orders, _, _) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var outsider = Guid.NewGuid();
        var order = AddOrder(orders, owner, worker);

        Assert.Throws<UnauthorizedAccessException>(() => service.List(order.Id, outsider, 50));
        Assert.Throws<UnauthorizedAccessException>(() => service.Send(order.Id, outsider, "我可以插话吗"));
        Assert.Throws<UnauthorizedAccessException>(() => service.MarkRead(order.Id, outsider));
    }

    [Fact]
    public void Unknown_order_is_reported_as_missing()
    {
        var (service, _, _, _) = CreateService();

        Assert.Throws<KeyNotFoundException>(() => service.List(Guid.NewGuid(), Guid.NewGuid(), 50));
    }

    [Fact]
    public void Marking_read_clears_the_unread_count_for_the_reader_only()
    {
        var (service, orders, _, _) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var order = AddOrder(orders, owner, worker);
        service.Send(order.Id, worker, "我到了");
        service.Send(order.Id, worker, "在门口等你");

        Assert.Equal(2, service.List(order.Id, owner, 50).UnreadCount);

        var remaining = service.MarkRead(order.Id, owner);

        Assert.Equal(0, remaining);
        Assert.Equal(0, service.List(order.Id, owner, 50).UnreadCount);
        // 服务者自己的消息对他本人本来就不算未读。
        Assert.Equal(0, service.List(order.Id, worker, 50).UnreadCount);
    }

    [Fact]
    public void Unread_by_order_feeds_the_order_list_badge()
    {
        var (service, orders, _, _) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var first = AddOrder(orders, owner, worker);
        var second = AddOrder(orders, owner, worker);
        service.Send(first.Id, worker, "一");
        service.Send(second.Id, worker, "二");
        service.Send(second.Id, worker, "三");

        var unread = service.UnreadByOrder(owner, [first.Id, second.Id, Guid.NewGuid()]);

        Assert.Equal(1, unread[first.Id]);
        Assert.Equal(2, unread[second.Id]);
        Assert.Equal(2, unread.Count);
        Assert.Empty(service.UnreadByOrder(owner, []));
    }

    [Fact]
    public void Content_is_validated()
    {
        var (service, orders, _, _) = CreateService();
        var owner = Guid.NewGuid();
        var order = AddOrder(orders, owner, Guid.NewGuid());

        Assert.Throws<DomainException>(() => service.Send(order.Id, owner, "   "));
        Assert.Throws<DomainException>(() => service.Send(order.Id, owner, new string('好', OrderMessage.MaxContentLength + 1)));
    }

    private static (OrderChatService Service, InMemoryOrderRepository Orders, InMemoryNotificationRepository Notifications, MutableTimeProvider Clock) CreateService()
    {
        var orders = new InMemoryOrderRepository();
        var messages = new InMemoryOrderMessageRepository();
        var notifications = new InMemoryNotificationRepository();
        var clock = new MutableTimeProvider(Now);
        var notificationService = new NotificationService(notifications, clock);
        var service = new OrderChatService(orders, messages, notificationService, new InMemoryUnitOfWork(), clock);
        return (service, orders, notifications, clock);
    }

    private static Order AddOrder(InMemoryOrderRepository orders, Guid owner, Guid worker)
    {
        var order = new Order(Guid.NewGuid(), owner, worker, "代取文件", new Money(50), Now);
        orders.Add(order);
        return order;
    }
}
