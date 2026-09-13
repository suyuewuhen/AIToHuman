using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>争议的用例层行为：发起、冻结、运营三种处置结果、审计与双方通知。</summary>
public sealed class DisputeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opening_a_dispute_notifies_the_other_party_and_freezes_the_order()
    {
        var world = new World();
        var (orderId, taskId) = world.PrepareSubmittedOrder();

        var order = world.Service.OpenDispute(orderId, world.Owner, "交付内容与验收标准不符");

        Assert.Equal("Disputed", order.Status);
        Assert.Equal("交付内容与验收标准不符", order.DisputeReason);

        var notification = Assert.Single(world.Inbox(world.Worker), item => item.Type == NotificationTypes.OrderDisputed);
        Assert.Equal(orderId, notification.Payload.GetProperty("orderId").GetGuid());

        // 争议期间任务保持 Assigned（订单还在，任务不能回大厅）。
        Assert.Equal("Assigned", world.Service.Get(taskId)!.Status);
        Assert.Throws<DomainException>(() => world.Service.CancelOrder(orderId, world.Owner, "算了"));
        Assert.Throws<DomainException>(() => world.Service.ApproveOrder(orderId, world.Owner, "通过"));
    }

    [Fact]
    public void Worker_can_dispute_a_rejection_but_not_a_submission()
    {
        var world = new World();
        var (orderId, _) = world.PrepareSubmittedOrder();

        Assert.Throws<DomainException>(() => world.Service.OpenDispute(orderId, world.Worker, "我不服"));

        world.Service.RejectOrder(orderId, world.Owner, "照片看不清");
        var disputed = world.Service.OpenDispute(orderId, world.Worker, "驳回理由不成立");

        Assert.Equal("Disputed", disputed.Status);
        Assert.Single(world.Inbox(world.Owner), item => item.Type == NotificationTypes.OrderDisputed);
    }

    [Fact]
    public void Admin_approving_a_dispute_completes_the_order_closes_the_task_and_tells_both_parties()
    {
        var world = new World();
        var (orderId, taskId) = world.PrepareSubmittedOrder();
        world.Service.OpenDispute(orderId, world.Owner, "交付不符");

        var resolved = world.Admin.ResolveDispute(orderId, "Approve", "凭证符合验收标准，判定完成", world.AdminId);

        Assert.Equal("Approved", resolved.Status);
        Assert.Equal("Approve", resolved.DisputeResolution);
        Assert.Equal("凭证符合验收标准，判定完成", resolved.DisputeResolutionNote);
        Assert.Equal("Closed", world.Service.Get(taskId)!.Status);
        Assert.All(new[] { world.Owner, world.Worker }, user =>
            Assert.Single(world.Inbox(user), item => item.Type == NotificationTypes.OrderDisputeResolved));
        Assert.Contains(world.Audits(), item => item.Action == "order.dispute.approve" && item.TargetId == orderId);
    }

    [Fact]
    public void Admin_rework_sends_the_order_back_to_execution_and_keeps_the_task_assigned()
    {
        var world = new World();
        var (orderId, taskId) = world.PrepareSubmittedOrder();
        world.Service.OpenDispute(orderId, world.Owner, "交付不符");

        var resolved = world.Admin.ResolveDispute(orderId, "rework", "缺少取件码照片，请补交", world.AdminId);

        Assert.Equal("InProgress", resolved.Status);
        Assert.Equal(1, resolved.ReworkCount);
        Assert.Equal("缺少取件码照片，请补交", resolved.RejectionNote);
        Assert.Equal("Assigned", world.Service.Get(taskId)!.Status);
        Assert.Contains(world.Audits(), item => item.Action == "order.dispute.rework");

        // 服务者可以继续提交，返工闭环没被争议打断。
        var submitted = world.Service.SubmitOrder(orderId, world.Worker, "补了照片");
        Assert.Equal("Submitted", submitted.Status);
    }

    [Fact]
    public void Admin_cancelling_a_dispute_ends_the_order_and_releases_the_task()
    {
        var world = new World();
        var (orderId, taskId) = world.PrepareSubmittedOrder();

        // 服务者只能从 Rejected 发起，这里先把订单驳回到 Rejected 再发起。
        world.Service.RejectOrder(orderId, world.Owner, "需要补做");
        world.Service.OpenDispute(orderId, world.Worker, "双方谈不拢");

        var resolved = world.Admin.ResolveDispute(orderId, "Cancel", "证据不足以判定，终止订单", world.AdminId);

        Assert.Equal("Cancelled", resolved.Status);
        Assert.Null(resolved.CancelledBy);
        Assert.Equal("证据不足以判定，终止订单", resolved.CancellationReason);
        // 任务回到大厅，可以重新招募。
        Assert.Equal("Published", world.Service.Get(taskId)!.Status);
        Assert.Contains(world.Audits(), item => item.Action == "order.dispute.cancel");
    }

    [Fact]
    public void Admin_cancelling_a_dispute_after_the_deadline_expires_the_task()
    {
        var world = new World();
        var (orderId, taskId) = world.PrepareSubmittedOrder(deadline: Now.AddHours(1));
        world.Service.OpenDispute(orderId, world.Owner, "谈不拢");
        world.Clock.Advance(TimeSpan.FromHours(3));

        world.Admin.ResolveDispute(orderId, "Cancel", "终止订单", world.AdminId);

        Assert.Equal("Expired", world.Service.Get(taskId)!.Status);
    }

    [Fact]
    public void Dispute_resolution_is_all_or_nothing_and_audited()
    {
        var unitOfWork = new SpyUnitOfWork();
        var world = new World(unitOfWork);
        var (orderId, taskId) = world.PrepareSubmittedOrder();
        world.Service.OpenDispute(orderId, world.Owner, "交付不符");

        var executionsBefore = unitOfWork.Executions;
        var commitsBefore = unitOfWork.Commits;

        world.Admin.ResolveDispute(orderId, "Approve", "判定完成", world.AdminId);

        Assert.Equal(executionsBefore + 1, unitOfWork.Executions);
        Assert.Equal(commitsBefore + 1, unitOfWork.Commits);
        Assert.Equal("Closed", world.Service.Get(taskId)!.Status);
        Assert.Single(world.Audits());
    }

    [Fact]
    public void Only_disputed_orders_can_be_resolved_and_the_decision_must_be_known()
    {
        var world = new World();
        var (orderId, _) = world.PrepareSubmittedOrder();

        Assert.Throws<DomainException>(() => world.Admin.ResolveDispute(orderId, "Approve", "还没争议", world.AdminId));

        world.Service.OpenDispute(orderId, world.Owner, "交付不符");
        var error = Assert.Throws<DomainException>(() => world.Admin.ResolveDispute(orderId, "Refund", "退钱", world.AdminId));
        Assert.Contains("争议处置结果", error.Message);
    }

    [Fact]
    public void Admin_order_search_defaults_to_pending_disputes()
    {
        var world = new World();
        var (disputedOrderId, _) = world.PrepareSubmittedOrder();
        world.Service.OpenDispute(disputedOrderId, world.Owner, "交付不符");

        var (otherOrderId, _) = world.PrepareSubmittedOrder();

        var pending = world.Admin.SearchOrders(null, 20).Items;
        Assert.Single(pending, item => item.Id == disputedOrderId);
        Assert.DoesNotContain(pending, item => item.Id == otherOrderId);
        // 参与者邮箱与争议原因都要给运营看清楚。
        Assert.Equal(world.OwnerEmail, pending.Single().OwnerEmail);
        Assert.Equal("交付不符", pending.Single().DisputeReason);

        Assert.Equal(2, world.Admin.SearchOrders("all", 20).Items.Count);
        Assert.Throws<DomainException>(() => world.Admin.SearchOrders("NoSuchStatus", 20));
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();
        private readonly InMemoryAdminAuditRepository audits = new();

        public World(IUnitOfWork? unitOfWork = null)
        {
            Owner = Guid.NewGuid();
            Worker = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            OwnerEmail = "owner@example.com";
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            var work = unitOfWork ?? new InMemoryUnitOfWork();
            Service = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(), new EmptyUserDirectory(), Clock, Notifications, work);
            Admin = new AdminConsoleService(
                tasks,
                orders,
                new StubUserDirectory(Owner, OwnerEmail, Worker, "worker@example.com"),
                tasks,
                orders,
                audits,
                Clock,
                Notifications,
                work);
        }

        public Guid Owner { get; }
        public Guid Worker { get; }
        public Guid AdminId { get; }
        public string OwnerEmail { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }
        public AdminConsoleService Admin { get; }

        public IReadOnlyCollection<NotificationResponse> Inbox(Guid userId) => Notifications.List(userId, 20).Items;

        public IReadOnlyCollection<AdminAuditEntry> Audits() => audits.List(50);

        public (Guid OrderId, Guid TaskId) PrepareSubmittedOrder(DateTimeOffset? deadline = null)
        {
            var task = Service.Create(new CreateTaskRequest(Owner, "代取文件", "到前台取件", "朝阳区", deadline ?? Now.AddHours(6), 50, ["按时送达"]));
            Service.Publish(task.Id, Owner);
            var applied = Service.Apply(task.Id, new ApplyForTaskRequest(Worker, "半小时可到"));
            var order = Service.Select(task.Id, applied.Applications.Single().Id, new SelectApplicationRequest(Owner)).Order;
            Service.StartOrder(order.Id, Worker);
            Service.SubmitOrder(order.Id, Worker, "已完成");
            return (order.Id, task.Id);
        }

        private sealed class StubUserDirectory(Guid ownerId, string ownerEmail, Guid workerId, string workerEmail) : IUserDirectory
        {
            private readonly Dictionary<Guid, AdminUserView> users = new()
            {
                [ownerId] = new AdminUserView(ownerId, ownerEmail, "需求方", "owner", Now),
                [workerId] = new AdminUserView(workerId, workerEmail, "服务者", "worker", Now)
            };

            public IReadOnlyCollection<AdminUserView> Search(string? keyword, int limit) => users.Values.Take(limit).ToArray();

            public IReadOnlyDictionary<Guid, AdminUserView> FindMany(IReadOnlyCollection<Guid> ids) =>
                users.Where(item => ids.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Value);
        }
    }

    private sealed class SpyUnitOfWork : IUnitOfWork
    {
        public int Executions { get; private set; }
        public int Commits { get; private set; }

        public void Execute(Action operation)
        {
            Executions++;
            operation();
            Commits++;
        }
    }
}
