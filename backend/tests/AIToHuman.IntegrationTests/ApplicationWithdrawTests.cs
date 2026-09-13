using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>服务者撤回报名、报名截止时间与“我的报名”列表的用例层行为。</summary>
public sealed class ApplicationWithdrawTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Withdrawing_an_application_notifies_the_owner_and_lets_the_worker_apply_again()
    {
        var world = new World();
        var taskId = world.PublishTask();
        var applicationId = world.Apply(taskId, world.Worker);

        var result = world.Service.WithdrawApplication(taskId, applicationId, world.Worker);

        var application = Assert.Single(result.Applications, item => item.Id == applicationId);
        Assert.Equal("Withdrawn", application.Status);

        var notification = Assert.Single(world.Inbox(world.Owner), item => item.Type == NotificationTypes.TaskApplicationWithdrawn);
        Assert.Equal(taskId, notification.Payload.GetProperty("taskId").GetGuid());
        Assert.Equal(applicationId, notification.Payload.GetProperty("applicationId").GetGuid());
        Assert.Equal(world.Worker, notification.Payload.GetProperty("workerId").GetGuid());

        // 撤回后可以重新报名，两条记录都留着。
        var again = world.Service.Apply(taskId, new ApplyForTaskRequest(world.Worker, "又想接了"));
        Assert.Equal(2, again.Applications.Count);
        Assert.Equal("Pending", again.Applications.Single(item => item.Status == "Pending").Status);
    }

    [Fact]
    public void Withdrawal_writes_the_task_and_the_notification_in_one_unit_of_work()
    {
        var unitOfWork = new SpyUnitOfWork();
        var world = new World(unitOfWork);
        var taskId = world.PublishTask();
        var applicationId = world.Apply(taskId, world.Worker);

        var executionsBefore = unitOfWork.Executions;
        var commitsBefore = unitOfWork.Commits;

        world.Service.WithdrawApplication(taskId, applicationId, world.Worker);

        Assert.Equal(executionsBefore + 1, unitOfWork.Executions);
        Assert.Equal(commitsBefore + 1, unitOfWork.Commits);
    }

    [Fact]
    public void A_worker_cannot_withdraw_somebody_elses_application()
    {
        var world = new World();
        var taskId = world.PublishTask();
        var applicationId = world.Apply(taskId, world.Worker);

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.WithdrawApplication(taskId, applicationId, world.OtherWorker));
    }

    [Fact]
    public void Applications_are_refused_once_the_application_deadline_passed()
    {
        var world = new World();
        var task = world.Service.Create(new CreateTaskRequest(
            world.Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"],
            ApplicationDeadline: Now.AddHours(1)));
        world.Service.Publish(task.Id, world.Owner);
        world.Service.Apply(task.Id, new ApplyForTaskRequest(world.Worker, "可以"));

        world.Clock.Advance(TimeSpan.FromHours(2));
        var error = Assert.Throws<DomainException>(() => world.Service.Apply(task.Id, new ApplyForTaskRequest(world.OtherWorker, "我来")));

        Assert.Contains("报名已经截止", error.Message);
        // 大厅里仍然看得到这条任务（还没到任务截止时间），但已经不能报名了。
        var summary = world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items.Single();
        Assert.False(summary.AcceptingApplications);
        Assert.Equal(Now.AddHours(1), summary.ApplicationDeadline);
        // 之前报过名的人仍然可以被选中，订单照常成立。
        var pending = world.Service.Get(task.Id)!.Applications.Single();
        var selected = world.Service.Select(task.Id, pending.Id, new SelectApplicationRequest(world.Owner));
        Assert.Equal("Accepted", selected.Order.Status);
    }

    [Fact]
    public void Tasks_without_an_application_deadline_keep_accepting_applications()
    {
        var world = new World();
        var taskId = world.PublishTask();

        var summary = world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items.Single(item => item.Id == taskId);

        Assert.True(summary.AcceptingApplications);
        Assert.Null(summary.ApplicationDeadline);
    }

    [Fact]
    public void My_applications_list_shows_every_state_and_whether_it_can_still_be_withdrawn()
    {
        var world = new World();
        var openTask = world.PublishTask();
        var pending = world.Apply(openTask, world.Worker);

        var withdrawnTask = world.PublishTask();
        var toWithdraw = world.Apply(withdrawnTask, world.Worker);
        world.Service.WithdrawApplication(withdrawnTask, toWithdraw, world.Worker);

        var selectedTask = world.PublishTask();
        var selected = world.Apply(selectedTask, world.Worker);
        world.Service.Select(selectedTask, selected, new SelectApplicationRequest(world.Owner));

        var mine = world.Service.ListMyApplications(world.Worker, 20).Items;

        Assert.Equal(3, mine.Count);
        Assert.True(mine.Single(item => item.ApplicationId == pending).CanWithdraw);
        Assert.False(mine.Single(item => item.ApplicationId == toWithdraw).CanWithdraw);
        Assert.False(mine.Single(item => item.ApplicationId == selected).CanWithdraw);
        Assert.Equal("Withdrawn", mine.Single(item => item.ApplicationId == toWithdraw).ApplicationStatus);
        Assert.Equal("Selected", mine.Single(item => item.ApplicationId == selected).ApplicationStatus);
        // 别的服务者看不到我的报名。
        Assert.Empty(world.Service.ListMyApplications(world.OtherWorker, 20).Items);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();

        public World(IUnitOfWork? unitOfWork = null)
        {
            Owner = Guid.NewGuid();
            Worker = Guid.NewGuid();
            OtherWorker = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Service = new TaskService(
                tasks,
                new InMemoryOrderRepository(),
                new InMemoryReviewRepository(),
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

        public IReadOnlyCollection<NotificationResponse> Inbox(Guid userId) => Notifications.List(userId, 20).Items;

        public Guid PublishTask()
        {
            var task = Service.Create(new CreateTaskRequest(Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));
            Service.Publish(task.Id, Owner);
            return task.Id;
        }

        public Guid Apply(Guid taskId, Guid workerId)
        {
            var applied = Service.Apply(taskId, new ApplyForTaskRequest(workerId, "半小时可到"));
            return applied.Applications.Single(item => item.WorkerId == workerId && item.Status == "Pending").Id;
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
