using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 用例层的写入边界：并发敏感的操作必须走同一个工作单元，且失败时不提交。
/// 真正的行级冲突由 PostgreSQL 上的并发令牌负责，这里验证边界本身。
/// </summary>
public sealed class TaskServiceUnitOfWorkTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Select_assigns_the_task_and_creates_the_order_inside_one_unit_of_work()
    {
        var (service, unitOfWork) = CreateService();
        var owner = Guid.NewGuid();
        var applicationId = PrepareApplication(service, owner, out var taskId);

        var result = service.Select(taskId, applicationId, new SelectApplicationRequest(owner));

        Assert.Equal(1, unitOfWork.Executions);
        Assert.Equal(1, unitOfWork.Commits);
        Assert.Equal("Assigned", result.Task.Status);
        Assert.Equal("Accepted", result.Order.Status);
    }

    [Fact]
    public void Select_does_not_commit_when_writing_the_order_fails()
    {
        var unitOfWork = new SpyUnitOfWork();
        var (service, _) = CreateService(unitOfWork, new ThrowingOrderRepository());
        var owner = Guid.NewGuid();
        var applicationId = PrepareApplication(service, owner, out var taskId);

        Assert.Throws<InvalidOperationException>(() => service.Select(taskId, applicationId, new SelectApplicationRequest(owner)));

        Assert.Equal(1, unitOfWork.Executions);
        Assert.Equal(0, unitOfWork.Commits);
    }

    [Fact]
    public void Approve_writes_the_order_and_closes_the_task_inside_one_unit_of_work()
    {
        var (service, unitOfWork) = CreateService();
        var owner = Guid.NewGuid();
        var worker = Guid.NewGuid();
        var applicationId = PrepareApplication(service, owner, worker, out var taskId);
        var order = service.Select(taskId, applicationId, new SelectApplicationRequest(owner)).Order;
        service.StartOrder(order.Id, worker);
        service.SubmitOrder(order.Id, worker, "已完成");

        var executionsBeforeApprove = unitOfWork.Executions;
        var commitsBeforeApprove = unitOfWork.Commits;

        var approved = service.ApproveOrder(order.Id, owner, "验收通过");

        Assert.Equal(executionsBeforeApprove + 1, unitOfWork.Executions);
        Assert.Equal(commitsBeforeApprove + 1, unitOfWork.Commits);
        Assert.Equal("Approved", approved.Status);
        Assert.Equal("Closed", service.Get(taskId)!.Status);
    }

    private static Guid PrepareApplication(TaskService service, Guid owner, out Guid taskId) => PrepareApplication(service, owner, Guid.NewGuid(), out taskId);

    private static Guid PrepareApplication(TaskService service, Guid owner, Guid worker, out Guid taskId)
    {
        var task = service.Create(new CreateTaskRequest(owner, "代取文件", "到前台取一份普通文件", "浦东新区", Now.AddHours(6), 50, ["上传取件码照片"]));
        service.Publish(task.Id, owner);
        var applied = service.Apply(task.Id, new ApplyForTaskRequest(worker, "半小时可到"));
        taskId = task.Id;
        return applied.Applications.Single().Id;
    }

    private static (TaskService Service, SpyUnitOfWork UnitOfWork) CreateService() =>
        CreateService(new SpyUnitOfWork(), new InMemoryOrderRepository());

    private static (TaskService Service, SpyUnitOfWork UnitOfWork) CreateService(SpyUnitOfWork unitOfWork, IOrderRepository orderRepository)
    {
        var clock = new FixedTimeProvider(Now);
        var service = new TaskService(
            new InMemoryTaskRepository(),
            orderRepository,
            new InMemoryReviewRepository(),
            clock,
            new NotificationService(new InMemoryNotificationRepository(), clock),
            unitOfWork);
        return (service, unitOfWork);
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

    private sealed class ThrowingOrderRepository : IOrderRepository
    {
        public Order? Get(Guid id) => null;
        public Order? GetByTask(Guid taskId) => null;
        public IReadOnlyCollection<Order> ListByUser(Guid userId) => [];
        public void Add(Order order) => throw new InvalidOperationException("模拟订单写入失败。");
        public void Save(Order order) => throw new InvalidOperationException("模拟订单写入失败。");
    }
}
