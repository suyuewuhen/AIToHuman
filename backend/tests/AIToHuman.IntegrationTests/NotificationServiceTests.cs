using AIToHuman.Application.Notifications;
using AIToHuman.Application.Common;
using AIToHuman.Application.Tasks;
using AIToHuman.Api.Notifications;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Notifications;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace AIToHuman.IntegrationTests;

/// <summary>通知骨干：信封、幂等去重、收件箱与未读数、Outbox 派发。</summary>
public sealed class NotificationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Order_created_notifies_the_worker_with_a_versioned_envelope()
    {
        var (service, repository, _) = CreateService();
        var order = NewOrder();

        service.EnqueueOrderCreated(order, Now);

        var notification = Assert.Single(repository.ListByUser(order.WorkerId, 10));
        Assert.Equal(NotificationTypes.OrderCreated, notification.Type);
        Assert.Equal(NotificationService.EnvelopeVersion, notification.Version);
        Assert.Equal(order.Id, notification.EventId);
        Assert.Null(notification.DispatchedAt);
        Assert.False(notification.IsRead);

        var envelope = service.ToEnvelope(notification);
        Assert.Equal(order.Id, envelope.EventId);
        Assert.Equal(NotificationTypes.OrderCreated, envelope.Type);
        Assert.Equal(NotificationService.EnvelopeVersion, envelope.Version);
        Assert.Equal(Now, envelope.OccurredAt);
        Assert.Equal(order.Id, envelope.Payload.GetProperty("orderId").GetGuid());
        Assert.Equal("代取文件", envelope.Payload.GetProperty("title").GetString());
        Assert.Equal("Accepted", envelope.Payload.GetProperty("status").GetString());
    }

    [Fact]
    public void Enqueueing_the_same_event_twice_keeps_a_single_notification()
    {
        var (service, repository, _) = CreateService();
        var order = NewOrder();

        service.EnqueueOrderCreated(order, Now);
        service.EnqueueOrderCreated(order, Now.AddMinutes(1));

        Assert.Single(repository.ListByUser(order.WorkerId, 10));
        Assert.Equal(1, repository.CountUnread(order.WorkerId));
    }

    [Fact]
    public void Status_change_uses_a_deterministic_event_id_per_status()
    {
        var (service, repository, _) = CreateService();
        var owner = Guid.NewGuid();
        var order = NewOrder(owner);

        service.EnqueueOrderStatusChanged(order, owner, Now);
        service.EnqueueOrderStatusChanged(order, owner, Now.AddMinutes(1));

        Assert.Single(repository.ListByUser(owner, 10));

        // 补上提交凭证后的状态变化应该产生第二条通知。
        var submitted = Order.Rehydrate(order.Id, order.TaskId, order.OwnerId, order.WorkerId, order.Title, order.Reward, OrderStatus.Submitted, Now);
        service.EnqueueOrderStatusChanged(submitted, owner, Now.AddMinutes(2));

        Assert.Equal(2, repository.ListByUser(owner, 10).Count);
    }

    [Fact]
    public void Notifications_without_a_recipient_are_ignored()
    {
        var (service, repository, _) = CreateService();

        service.EnqueueOrderStatusChanged(NewOrder(), Guid.Empty, Now);

        Assert.Empty(repository.ListPendingDispatch(10));
    }

    [Fact]
    public void Inbox_lists_newest_first_and_reports_unread_count()
    {
        var (service, _, clock) = CreateService();
        var worker = Guid.NewGuid();
        var first = NewOrder(workerId: worker);
        service.EnqueueOrderCreated(first, Now);
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = NewOrder(workerId: worker);
        service.EnqueueOrderCreated(second, clock.GetUtcNow());

        var list = service.List(worker, 10);

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(second.Id, list.Items[0].EventId);
        Assert.Equal(2, list.UnreadCount);
    }

    [Fact]
    public void Marking_read_supports_single_ids_and_mark_all()
    {
        var (service, repository, _) = CreateService();
        var worker = Guid.NewGuid();
        service.EnqueueOrderCreated(NewOrder(workerId: worker), Now);
        service.EnqueueOrderCreated(NewOrder(workerId: worker), Now.AddMinutes(1));
        var items = service.List(worker, 10).Items;

        var afterSingle = service.MarkRead(worker, [items[0].Id]);
        Assert.Equal(1, afterSingle);
        Assert.Equal(1, service.UnreadCount(worker));

        var afterAll = service.MarkRead(worker, null);
        Assert.Equal(0, afterAll);
        Assert.Equal(0, service.UnreadCount(worker));
        Assert.All(repository.ListByUser(worker, 10), item => Assert.True(item.IsRead));
    }

    [Fact]
    public void Marking_read_ignores_notifications_of_other_users()
    {
        var (service, _, _) = CreateService();
        var worker = Guid.NewGuid();
        service.EnqueueOrderCreated(NewOrder(workerId: worker), Now);
        var item = service.List(worker, 10).Items.Single();

        var unread = service.MarkRead(Guid.NewGuid(), [item.Id]);

        // 返回的是调用者自己的未读数（他没有通知），而这条通知本身不能被别人标记为已读。
        Assert.Equal(0, unread);
        Assert.Equal(1, service.UnreadCount(worker));
    }

    [Fact]
    public async Task Dispatcher_pushes_pending_notifications_to_the_user_group_and_marks_them_dispatched()
    {
        var repository = new InMemoryNotificationRepository();
        var clock = new FixedTimeProvider(Now);
        var services = new ServiceCollection();
        services.AddSingleton<INotificationRepository>(repository);
        services.AddSingleton<TimeProvider>(clock);
        services.AddScoped<NotificationService>();
        using var provider = services.BuildServiceProvider();

        var hub = new RecordingHubContext();
        var dispatcher = new NotificationDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), hub, new FakeFanout(), NullLogger<NotificationDispatcher>.Instance);
        var worker = Guid.NewGuid();
        var notificationService = provider.GetRequiredService<NotificationService>();
        notificationService.EnqueueOrderCreated(NewOrder(workerId: worker), Now);

        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        var sent = Assert.Single(hub.Recorded);
        Assert.Equal($"user:{worker:N}", sent.Group);
        Assert.Equal("notification.created", sent.Method);
        var envelope = Assert.IsType<NotificationEnvelope>(sent.Payload);
        Assert.Equal(NotificationTypes.OrderCreated, envelope.Type);

        // 已派发的不再出现在 Outbox 里，重复派发也不会再推一次。
        Assert.Empty(notificationService.ListPendingDispatch(10));
        await dispatcher.DispatchPendingAsync(CancellationToken.None);
        Assert.Single(hub.Recorded);
    }

    [Fact]
    public void Claiming_a_pending_notification_is_exclusive_across_instances()
    {
        var repository = new InMemoryNotificationRepository();
        var notification = new Notification(Guid.NewGuid(), Guid.NewGuid(), NotificationTypes.OrderCreated, 1, "{}", Now);
        repository.Add(notification);

        // 只有第一个认领者拿到 true，第二个实例扫到同一条记录也不会重复推送。
        Assert.True(repository.TryClaim(notification.Id, Now));
        Assert.False(repository.TryClaim(notification.Id, Now.AddSeconds(1)));

        // 推送失败撤回认领后，记录回到 Outbox，可以被重新认领。
        repository.ReleaseDispatch(notification.Id);
        Assert.True(repository.TryClaim(notification.Id, Now.AddSeconds(2)));
    }

    [Fact]
    public async Task Fanout_enabled_dispatcher_broadcasts_instead_of_pushing_locally()
    {
        var harness = CreateDispatcherHarness();
        using var harnessScope = harness;
        harness.Fanout.Enabled = true;
        var worker = Guid.NewGuid();
        harness.Service().EnqueueOrderCreated(NewOrder(workerId: worker), Now);

        await harness.Dispatcher().DispatchPendingAsync(CancellationToken.None);

        // 启用扇出后本实例不直接推给客户端：由订阅方决定哪个实例真正持有连接。
        Assert.Empty(harness.Hub.Recorded);
        var broadcast = Assert.Single(harness.Fanout.Published);
        Assert.Equal(worker, broadcast.UserId);
        Assert.Empty(harness.Service().ListPendingDispatch(10));

        // 广播消息还原出的信封必须与本地推送完全一致，否则客户端会看到两种格式的通知。
        var envelope = NotificationService.ToEnvelope(broadcast);
        Assert.Equal(broadcast.EventId, envelope.EventId);
        Assert.Equal(NotificationTypes.OrderCreated, envelope.Type);
        Assert.Equal(NotificationService.EnvelopeVersion, envelope.Version);
        Assert.Equal(Now, envelope.OccurredAt);
        Assert.Equal(broadcast.EventId, envelope.Payload.GetProperty("orderId").GetGuid());
        Assert.Equal("代取文件", envelope.Payload.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Failed_broadcast_falls_back_to_pushing_the_local_clients()
    {
        var harness = CreateDispatcherHarness();
        using var harnessScope = harness;
        harness.Fanout.Enabled = true;
        harness.Fanout.PublishFailure = new InvalidOperationException("redis 不可用");
        var worker = Guid.NewGuid();
        harness.Service().EnqueueOrderCreated(NewOrder(workerId: worker), Now);

        await harness.Dispatcher().DispatchPendingAsync(CancellationToken.None);

        // 广播不通时不能什么都不推：兜底推给连在本实例上的客户端，通知照常算已派发。
        var sent = Assert.Single(harness.Hub.Recorded);
        Assert.Equal($"user:{worker:N}", sent.Group);
        Assert.Equal("notification.created", sent.Method);
        Assert.Empty(harness.Fanout.Published);
        Assert.Empty(harness.Service().ListPendingDispatch(10));
    }

    [Fact]
    public async Task Notification_returns_to_the_outbox_when_both_broadcast_and_local_push_fail()
    {
        var harness = CreateDispatcherHarness();
        using var harnessScope = harness;
        harness.Fanout.Enabled = true;
        harness.Fanout.PublishFailure = new InvalidOperationException("redis 不可用");
        harness.Hub.Fail = true;
        harness.Service().EnqueueOrderCreated(NewOrder(), Now);

        var dispatcher = harness.Dispatcher();
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        // 两条路都不通：认领被撤回，通知回到 Outbox 等下一轮，不会被静默标成“已推送”。
        Assert.Single(harness.Service().ListPendingDispatch(10));
        Assert.Empty(harness.Hub.Recorded);

        harness.Fanout.PublishFailure = null;
        harness.Hub.Fail = false;
        await dispatcher.DispatchPendingAsync(CancellationToken.None);

        Assert.Empty(harness.Service().ListPendingDispatch(10));
        Assert.Single(harness.Fanout.Published);
        Assert.Empty(harness.Hub.Recorded);
    }

    [Fact]
    public void Selecting_an_application_notifies_the_worker_and_transitions_notify_the_other_party()
    {
        var (notifications, repository, _) = CreateService();
        var taskService = new TaskService(
            new InMemoryTaskRepository(),
            new InMemoryOrderRepository(),
            new InMemoryReviewRepository(),
            new FixedTimeProvider(Now),
            notifications,
            new InMemoryUnitOfWork());

        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var task = taskService.Create(new CreateTaskRequest(owner, "代取文件", "到前台取一份普通文件", "浦东新区", Now.AddHours(6), 50, ["完成"]));
        taskService.Publish(task.Id, owner);
        var applied = taskService.Apply(task.Id, new ApplyForTaskRequest(worker, "可以"));
        var order = taskService.Select(task.Id, applied.Applications.Single().Id, new SelectApplicationRequest(owner)).Order;

        var workerInbox = repository.ListByUser(worker, 10).ToArray();
        Assert.Single(workerInbox);
        Assert.Equal(NotificationTypes.OrderCreated, workerInbox[0].Type);

        taskService.StartOrder(order.Id, worker);
        taskService.SubmitOrder(order.Id, worker, "已完成");

        var ownerInbox = repository.ListByUser(owner, 10).ToArray();
        Assert.Equal(2, ownerInbox.Length);
        Assert.All(ownerInbox, item => Assert.Equal(NotificationTypes.OrderStatusChanged, item.Type));
        Assert.Equal(NotificationService.EnvelopeVersion, ownerInbox[0].Version);
    }

    private static Order NewOrder(Guid? ownerId = null, Guid? workerId = null) =>
        new(Guid.NewGuid(), ownerId ?? Guid.NewGuid(), workerId ?? Guid.NewGuid(), "代取文件", new Money(50), Now);

    private static (NotificationService Service, InMemoryNotificationRepository Repository, MutableTimeProvider Clock) CreateService()
    {
        var repository = new InMemoryNotificationRepository();
        var clock = new MutableTimeProvider(Now);
        return (new NotificationService(repository, clock), repository, clock);
    }

    /// <summary>派发任务的最小宿主：内存仓储 + 固定时钟 + 记录型 Hub + 可切换行为的扇出实现。</summary>
    private static DispatcherHarness CreateDispatcherHarness()
    {
        var repository = new InMemoryNotificationRepository();
        var services = new ServiceCollection();
        services.AddSingleton<INotificationRepository>(repository);
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        services.AddScoped<NotificationService>();
        return new DispatcherHarness(services.BuildServiceProvider(), repository, new RecordingHubContext(), new FakeFanout());
    }

    private sealed record DispatcherHarness(
        ServiceProvider Provider,
        InMemoryNotificationRepository Repository,
        RecordingHubContext Hub,
        FakeFanout Fanout) : IDisposable
    {
        public NotificationService Service() => Provider.GetRequiredService<NotificationService>();

        public NotificationDispatcher Dispatcher() =>
            new(Provider.GetRequiredService<IServiceScopeFactory>(), Hub, Fanout, NullLogger<NotificationDispatcher>.Instance);

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>把扇出当成可插拔的开关：默认禁用（等价单实例部署），测试里可以打开或注入失败。</summary>
    private sealed class FakeFanout : INotificationFanout
    {
        public bool Enabled { get; set; }

        public List<NotificationFanoutMessage> Published { get; } = [];

        public Exception? PublishFailure { get; set; }

        public Task PublishAsync(NotificationFanoutMessage message, CancellationToken cancellationToken = default)
        {
            if (PublishFailure is { } failure) throw failure;
            Published.Add(message);
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(Func<NotificationFanoutMessage, CancellationToken, Task> handler, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingHubContext : IHubContext<NotificationsHub>
    {
        public List<(string Group, string Method, object? Payload)> Recorded { get; } = [];

        /// <summary>置为 true 时所有推送都抛错，用来验证“推不出去就退回 Outbox”。</summary>
        public bool Fail { get; set; }

        public IHubClients Clients { get; }

        public IGroupManager Groups => throw new NotSupportedException();

        public RecordingHubContext() => Clients = new RecordingClients(this);
    }

    private sealed class RecordingClients(RecordingHubContext owner) : IHubClients
    {
        public IClientProxy All => new RecordingProxy(owner, "all");
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => new RecordingProxy(owner, "all");
        public IClientProxy Client(string connectionId) => new RecordingProxy(owner, connectionId);
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => new RecordingProxy(owner, "clients");
        public IClientProxy Group(string groupName) => new RecordingProxy(owner, groupName);
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => new RecordingProxy(owner, "groups");
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new RecordingProxy(owner, groupName);
        public IClientProxy User(string userId) => new RecordingProxy(owner, userId);
        public IClientProxy Users(IReadOnlyList<string> userIds) => new RecordingProxy(owner, "users");
    }

    private sealed class RecordingProxy(RecordingHubContext owner, string group) : IClientProxy
    {
        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            if (owner.Fail) throw new InvalidOperationException("客户端推送失败");
            owner.Recorded.Add((group, method, args.FirstOrDefault()));
            return Task.CompletedTask;
        }
    }
}
