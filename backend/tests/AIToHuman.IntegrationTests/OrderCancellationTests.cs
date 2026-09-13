using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 履约异常流程：取消订单（谁能取消、任务去向、通知）与超过截止时间的任务过期扫描。
/// 全部走内存仓储与可推进的时钟，不依赖数据库；真实 PostgreSQL 上的端到端验证见 handoff 第 11 节。
/// </summary>
public sealed class OrderCancellationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_cancelling_reopens_the_task_and_notifies_the_worker()
    {
        var world = new World();
        var (taskId, worker, orderId) = world.PrepareOrder();

        var order = world.Service.CancelOrder(orderId, world.Owner, "临时不需要了");

        Assert.Equal("Cancelled", order.Status);
        Assert.Equal(world.Owner, order.CancelledBy);
        Assert.Equal("临时不需要了", order.CancellationReason);
        Assert.Equal(Now, order.CancelledAt);

        // 还没到截止时间：任务回到大厅等待重新招募，这次选择作废。
        var task = world.Service.Get(taskId)!;
        Assert.Equal("Published", task.Status);
        Assert.Equal("Rejected", task.Applications.Single().Status);
        Assert.Single(world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items);

        // 选人时服务者已经收到过 order.created，这里只关心新增的取消通知。
        var cancelled = Assert.Single(world.Inbox(worker), item => item.Type == NotificationTypes.OrderCancelled);
        Assert.Equal(orderId, cancelled.Payload.GetProperty("orderId").GetGuid());
    }

    [Fact]
    public void Worker_cancelling_before_starting_notifies_the_owner()
    {
        var world = new World();
        var (taskId, _, orderId) = world.PrepareOrder();

        world.Service.CancelOrder(orderId, world.Worker, "看错距离了");

        var notification = Assert.Single(world.Inbox(world.Owner));
        Assert.Equal(NotificationTypes.OrderCancelled, notification.Type);
        Assert.Equal("Published", world.Service.Get(taskId)!.Status);
    }

    [Fact]
    public void Worker_cannot_cancel_after_starting_and_the_order_stays_intact()
    {
        var world = new World();
        var (taskId, _, orderId) = world.PrepareOrder();
        world.Service.StartOrder(orderId, world.Worker);

        var error = Assert.Throws<DomainException>(() => world.Service.CancelOrder(orderId, world.Worker, "不想干了"));

        Assert.Contains("开始执行前", error.Message);
        Assert.Equal("InProgress", world.Service.ListOrders(world.Worker).Single().Status);
        Assert.Equal("Assigned", world.Service.Get(taskId)!.Status);
    }

    [Fact]
    public void Owner_cannot_cancel_after_the_worker_submitted()
    {
        var world = new World();
        var (taskId, _, orderId) = world.PrepareOrder();
        world.Service.StartOrder(orderId, world.Worker);
        world.Service.SubmitOrder(orderId, world.Worker, "已完成");

        var error = Assert.Throws<DomainException>(() => world.Service.CancelOrder(orderId, world.Owner, "不想验收"));

        Assert.Contains("先验收或驳回", error.Message);
        Assert.Equal("Assigned", world.Service.Get(taskId)!.Status);
    }

    [Fact]
    public void Cancelling_after_the_deadline_expires_the_task_and_tells_the_owner()
    {
        var world = new World();
        var (taskId, _, orderId) = world.PrepareOrder(deadline: Now.AddHours(1));
        world.Clock.Advance(TimeSpan.FromHours(3));

        world.Service.CancelOrder(orderId, world.Worker, "临时有事");

        var task = world.Service.Get(taskId)!;
        Assert.Equal("Expired", task.Status);
        Assert.Equal(Now.AddHours(3), task.ExpiredAt);

        // 取消者是服务者时，需求方还要知道任务已经彻底没了。
        var types = world.Inbox(world.Owner).Select(item => item.Type).ToArray();
        Assert.Contains(NotificationTypes.OrderCancelled, types);
        Assert.Contains(NotificationTypes.TaskExpired, types);
    }

    [Fact]
    public void Cancelling_twice_is_rejected()
    {
        var world = new World();
        var (_, _, orderId) = world.PrepareOrder();
        world.Service.CancelOrder(orderId, world.Owner, "第一次");

        Assert.Throws<DomainException>(() => world.Service.CancelOrder(orderId, world.Owner, "第二次"));
    }

    [Fact]
    public void Cancellation_requires_a_reason()
    {
        var world = new World();
        var (_, _, orderId) = world.PrepareOrder();

        var error = Assert.Throws<DomainException>(() => world.Service.CancelOrder(orderId, world.Owner, "   "));

        Assert.Contains("必须填写原因", error.Message);
    }

    [Fact]
    public void Cancellation_writes_the_order_and_the_task_in_one_unit_of_work()
    {
        var unitOfWork = new SpyUnitOfWork();
        var world = new World(unitOfWork);
        var (taskId, _, orderId) = world.PrepareOrder();

        // 准备阶段（选人建单）本身也走工作单元，这里只看取消这一次的增量。
        var executionsBefore = unitOfWork.Executions;
        var commitsBefore = unitOfWork.Commits;

        world.Service.CancelOrder(orderId, world.Owner, "一起写");

        Assert.Equal(executionsBefore + 1, unitOfWork.Executions);
        Assert.Equal(commitsBefore + 1, unitOfWork.Commits);
        Assert.Equal("Published", world.Service.Get(taskId)!.Status);
    }

    [Fact]
    public void Overdue_scan_expires_published_tasks_and_invalidates_pending_applications()
    {
        var world = new World();
        var task = world.Service.Create(new CreateTaskRequest(world.Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(1), 50, ["按时送达"]));
        world.Service.Publish(task.Id, world.Owner);
        world.Service.Apply(task.Id, new ApplyForTaskRequest(world.Worker, "半小时可到"));
        world.Service.Apply(task.Id, new ApplyForTaskRequest(world.OtherWorker, "顺路"));

        // 截止时间还没到：什么都不做。
        Assert.Equal(new TaskExpiryResult(0, 0), world.Service.ExpireOverdueTasks(100));

        world.Clock.Advance(TimeSpan.FromHours(2));
        var result = world.Service.ExpireOverdueTasks(100);

        Assert.Equal(1, result.Expired);
        Assert.Equal(0, result.Skipped);

        var expired = world.Service.Get(task.Id)!;
        Assert.Equal("Expired", expired.Status);
        Assert.Equal(Now.AddHours(2), expired.ExpiredAt);
        Assert.All(expired.Applications, item => Assert.Equal("Expired", item.Status));

        // 所有者与两个报名者各收到一条，且事件键互不相同（否则只会入队第一条）。
        Assert.Single(world.Inbox(world.Owner));
        Assert.Single(world.Inbox(world.Worker));
        Assert.Single(world.Inbox(world.OtherWorker));
        Assert.All(world.Inbox(world.Worker), item => Assert.Equal(NotificationTypes.TaskExpired, item.Type));

        // 幂等：已经过期的任务不再出现在候选集里。
        Assert.Equal(new TaskExpiryResult(0, 0), world.Service.ExpireOverdueTasks(100));
    }

    [Fact]
    public void Overdue_scan_leaves_assigned_and_future_tasks_alone()
    {
        var world = new World();
        var (assignedTaskId, worker, _) = world.PrepareOrder(deadline: Now.AddHours(1));
        var future = world.Service.Create(new CreateTaskRequest(world.Owner, "未来任务", "还有人没报名", "朝阳区", Now.AddDays(2), 40, ["按时送达"]));
        world.Service.Publish(future.Id, world.Owner);

        world.Clock.Advance(TimeSpan.FromHours(2));
        var result = world.Service.ExpireOverdueTasks(100);

        Assert.Equal(0, result.Expired);
        // 已分配的任务归订单流程管，不能因为过了截止时间就从服务者名下消失。
        Assert.Equal("Assigned", world.Service.Get(assignedTaskId)!.Status);
        Assert.Equal("Published", world.Service.Get(future.Id)!.Status);
        Assert.DoesNotContain(world.Inbox(worker), item => item.Type == NotificationTypes.TaskExpired);
    }

    [Fact]
    public void Overdue_scan_respects_the_batch_limit()
    {
        var world = new World();
        for (var index = 0; index < 3; index++)
        {
            var task = world.Service.Create(new CreateTaskRequest(world.Owner, $"任务 {index}", "描述", "朝阳区", Now.AddMinutes(30), 40, ["按时送达"]));
            world.Service.Publish(task.Id, world.Owner);
        }

        world.Clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(2, world.Service.ExpireOverdueTasks(2).Expired);
        Assert.Equal(1, world.Service.ExpireOverdueTasks(2).Expired);
    }

    [Fact]
    public void One_failing_task_does_not_block_the_rest_of_the_batch()
    {
        var world = new World();
        var broken = world.Service.Create(new CreateTaskRequest(world.Owner, "会失败的任务", "描述", "朝阳区", Now.AddMinutes(30), 40, ["按时送达"]));
        var healthy = world.Service.Create(new CreateTaskRequest(world.Owner, "正常任务", "描述", "朝阳区", Now.AddMinutes(30), 40, ["按时送达"]));
        world.Service.Publish(broken.Id, world.Owner);
        world.Service.Publish(healthy.Id, world.Owner);
        world.BreakSaving(broken.Id);

        world.Clock.Advance(TimeSpan.FromHours(1));
        var result = world.Service.ExpireOverdueTasks(100);

        // 单条写入失败只跳过那一条，后面的任务照常处理。
        Assert.Equal(1, result.Expired);
        Assert.Equal(1, result.Skipped);
        Assert.Equal("Expired", world.Service.Get(healthy.Id)!.Status);
    }

    [Fact]
    public void Owner_can_withdraw_a_published_task_and_applicants_are_told()
    {
        var world = new World();
        var task = world.Service.Create(new CreateTaskRequest(world.Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));
        world.Service.Publish(task.Id, world.Owner);
        world.Service.Apply(task.Id, new ApplyForTaskRequest(world.Worker, "可以"));

        var cancelled = world.Service.CancelTask(task.Id, world.Owner, "自己已经处理完了");

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("自己已经处理完了", cancelled.CancellationReason);
        Assert.Equal(Now, cancelled.CancelledAt);
        Assert.Empty(world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items);

        var notification = Assert.Single(world.Inbox(world.Worker));
        Assert.Equal(NotificationTypes.TaskCancelled, notification.Type);
        Assert.Equal(task.Id, notification.Payload.GetProperty("taskId").GetGuid());
    }

    [Fact]
    public void Only_the_owner_can_withdraw_and_only_before_selection()
    {
        var world = new World();
        var task = world.Service.Create(new CreateTaskRequest(world.Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));
        world.Service.Publish(task.Id, world.Owner);

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.CancelTask(task.Id, world.Worker, "不是我的任务"));
        Assert.Throws<DomainException>(() => world.Service.CancelTask(task.Id, world.Owner, "  "));

        var application = world.Service.Apply(task.Id, new ApplyForTaskRequest(world.Worker, "可以"));
        world.Service.Select(task.Id, application.Applications.Single().Id, new SelectApplicationRequest(world.Owner));

        // 已经产生订单：必须走取消订单，而不是撤销任务。
        var error = Assert.Throws<DomainException>(() => world.Service.CancelTask(task.Id, world.Owner, "想直接撤销"));
        Assert.Contains("已经分配并产生订单", error.Message);
    }

    /// <summary>一套内存依赖 + 可推进时钟，模拟一次完整的“发布 → 报名 → 选人”之后的各种异常操作。</summary>
    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly FailingTaskRepository repository;

        public World(IUnitOfWork? unitOfWork = null)
        {
            Owner = Guid.NewGuid();
            Worker = Guid.NewGuid();
            OtherWorker = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            repository = new FailingTaskRepository(tasks);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);

            Service = new TaskService(
                repository,
                new InMemoryOrderRepository(),
                new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(), new EmptyUserDirectory(),
                Clock,
                Notifications,
                unitOfWork ?? new InMemoryUnitOfWork());
        }

        public Guid Owner { get; }
        public Guid Worker { get; }
        public Guid OtherWorker { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }

        /// <summary>让某条任务的写入失败，用来验证过期扫描不会被单条失败拖垮。</summary>
        public void BreakSaving(Guid taskId) => repository.Break(taskId);

        public IReadOnlyCollection<NotificationResponse> Inbox(Guid userId) => Notifications.List(userId, 20).Items;

        /// <summary>发布 → 报名 → 选人，返回任务与服务者、订单标识。</summary>
        public (Guid TaskId, Guid WorkerId, Guid OrderId) PrepareOrder(DateTimeOffset? deadline = null)
        {
            var task = Service.Create(new CreateTaskRequest(Owner, "代取文件", "到前台取件", "朝阳区", deadline ?? Now.AddHours(6), 50, ["按时送达"]));
            Service.Publish(task.Id, Owner);
            var applied = Service.Apply(task.Id, new ApplyForTaskRequest(Worker, "半小时可到"));
            var order = Service.Select(task.Id, applied.Applications.Single().Id, new SelectApplicationRequest(Owner)).Order;
            return (task.Id, Worker, order.Id);
        }
    }

    /// <summary>可以按任务 ID 让 Save 失败的装饰器，模拟“读到之后被并发改掉”。</summary>
    private sealed class FailingTaskRepository(InMemoryTaskRepository inner) : ITaskRepository
    {
        private readonly HashSet<Guid> broken = [];

        public void Break(Guid taskId) => broken.Add(taskId);

        public IReadOnlyCollection<TaskItem> ListPublished(PublishedTaskFilter filter) => inner.ListPublished(filter);
        public IReadOnlyCollection<TaskItem> ListByOwner(Guid ownerId) => inner.ListByOwner(ownerId);
        public IReadOnlyCollection<TaskItem> ListOverduePublished(DateTimeOffset now, int limit) => inner.ListOverduePublished(now, limit);
        public IReadOnlyCollection<TaskItem> ListByApplicant(Guid workerId, int limit) => inner.ListByApplicant(workerId, limit);
        public IReadOnlyCollection<TaskItem> ListPendingRiskReview(int limit) => inner.ListPendingRiskReview(limit);
        public IReadOnlyCollection<TaskItem> ListPendingRiskAppeal(int limit) => inner.ListPendingRiskAppeal(limit);
        public IReadOnlyCollection<TaskItem> ListRiskRecheckCandidates(int assessedRuleVersion, int limit) => inner.ListRiskRecheckCandidates(assessedRuleVersion, limit);
        public TaskItem? Get(Guid id) => inner.Get(id);
        public void Add(TaskItem task) => inner.Add(task);

        public void Save(TaskItem task)
        {
            if (broken.Contains(task.Id)) throw new InvalidOperationException("模拟并发写入冲突。");
            inner.Save(task);
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
