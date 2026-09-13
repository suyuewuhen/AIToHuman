using AIToHuman.Application.Notifications;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 精确执行地址的读取与留痕：披露口径与以前一致（所有者与被选中的服务者），
/// 但**每一次读取都留痕**——包括被拒绝的尝试，那才是审计里最该看的信号。
/// </summary>
public sealed class AddressAccessAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_the_owner_and_the_selected_worker_get_the_address()
    {
        var world = new World();
        var taskId = world.Publish("代取文件", "世纪大道 100 号前台");
        var otherWorker = Guid.NewGuid();
        var application = world.Service.Apply(taskId, new ApplyForTaskRequest(world.Worker, "半小时可到"));
        world.Service.Apply(taskId, new ApplyForTaskRequest(otherWorker, "一小时可到"));

        // 未选人前：所有者可以，报名的服务者不行。
        Assert.Equal("世纪大道 100 号前台", world.Access.Read(taskId, world.Owner).Address);
        Assert.Throws<UnauthorizedAccessException>(() => world.Access.Read(taskId, world.Worker));

        world.Service.Select(taskId, application.Applications.Single().Id, new SelectApplicationRequest(world.Owner));

        Assert.Equal("世纪大道 100 号前台", world.Access.Read(taskId, world.Owner).Address);
        Assert.Equal("世纪大道 100 号前台", world.Access.Read(taskId, world.Worker).Address);
        Assert.Throws<UnauthorizedAccessException>(() => world.Access.Read(taskId, otherWorker));
    }

    [Fact]
    public void Every_read_is_kept_including_the_denied_ones()
    {
        var world = new World();
        var taskId = world.Publish("代取文件", "世纪大道 100 号前台");
        var stranger = Guid.NewGuid();

        world.Access.Read(taskId, world.Owner);
        Assert.Throws<UnauthorizedAccessException>(() => world.Access.Read(taskId, stranger));
        Assert.Throws<UnauthorizedAccessException>(() => world.Access.Read(taskId, Guid.Empty));

        var entries = world.AccessRepository.List(taskId, null, 20);

        // 三次读取全部留痕：一次披露 + 一次越权尝试 + 一次匿名尝试。
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, item => item.Role == AddressAccessRole.Owner && item.Outcome == AddressAccessOutcome.Granted && item.Disclosed);
        Assert.Contains(entries, item => item.ViewerId == stranger && item.Role == AddressAccessRole.Other && item.Outcome == AddressAccessOutcome.Denied);
        // 匿名请求记成"没有 viewer id"，而不是随便填一个空 Guid。
        Assert.Contains(entries, item => item.ViewerId is null && item.Outcome == AddressAccessOutcome.Denied);
        Assert.Equal(2, world.AccessRepository.CountDenied(taskId, null));
    }

    [Fact]
    public void A_task_without_an_address_records_no_disclosure_instead_of_a_denial()
    {
        var world = new World();
        var taskId = world.Publish("没有地址", null);

        var result = world.Access.Read(taskId, world.Owner);

        Assert.Null(result.Address);
        Assert.Equal(AddressAccessOutcome.NotSet, result.Outcome);
        var entry = Assert.Single(world.AccessRepository.List(taskId, null, 10));
        Assert.Equal(AddressAccessOutcome.NotSet, entry.Outcome);
        // "这条任务没有登记地址"不是越权尝试，不该计进拒绝次数。
        Assert.Equal(0, world.AccessRepository.CountDenied(taskId, null));
    }

    [Fact]
    public void The_audit_can_be_read_by_task_and_by_viewer()
    {
        var world = new World();
        var first = world.Publish("代取文件", "世纪大道 100 号前台");
        var second = world.Publish("代送文件", "南京西路 200 号前台");

        world.Access.Read(first, world.Owner);
        world.Access.Read(second, world.Owner);
        Assert.Throws<UnauthorizedAccessException>(() => world.Access.Read(first, world.Worker));

        var byTask = world.Access.ListAudits(first, null, 20);
        Assert.Equal(2, byTask.Items.Count);
        Assert.Equal(1, byTask.DeniedCount);
        Assert.All(byTask.Items, item => Assert.Equal(first, item.TaskId));

        var byViewer = world.Access.ListAudits(null, world.Owner, 20);
        Assert.Equal(2, byViewer.Items.Count);
        Assert.Equal(0, byViewer.DeniedCount);

        var everything = world.Access.ListAudits(null, null, 20);
        Assert.Equal(3, everything.Items.Count);
        // 运营看到的每一条都带身份说明：所有者 / 其他，以及是否真的披露了。
        Assert.Contains(everything.Items, item => item.ViewerRole == "Owner" && item.Disclosed);
        Assert.Contains(everything.Items, item => item.ViewerRole == "Other" && !item.Disclosed);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();

        public World()
        {
            Owner = Guid.NewGuid();
            Worker = Guid.NewGuid();
            Clock = new FixedTimeProvider(Now);
            AccessRepository = new InMemoryAddressAccessRepository();
            var unitOfWork = new InMemoryUnitOfWork();

            Service = new TaskService(
                tasks, new InMemoryOrderRepository(), new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(),
                new EmptyUserDirectory(), Clock, new NotificationService(new InMemoryNotificationRepository(), Clock), unitOfWork);
            Access = new AddressAccessService(tasks, AccessRepository, new EmptyUserDirectory(), Clock, unitOfWork);
        }

        public Guid Owner { get; }
        public Guid Worker { get; }
        public FixedTimeProvider Clock { get; }
        public InMemoryAddressAccessRepository AccessRepository { get; }
        public TaskService Service { get; }
        public AddressAccessService Access { get; }

        public Guid Publish(string title, string? executionAddress)
        {
            var task = Service.Create(new CreateTaskRequest(Owner, title, $"{title} 的描述", "浦东新区", Now.AddHours(6), 50, ["完成"], executionAddress));
            Service.Publish(task.Id, Owner);
            return task.Id;
        }
    }
}
