using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Risk;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 发布后风险复检的用例层行为：已经在线（甚至已经被接单）的任务，在规则升级之后会被怎么处置。
/// 这一组用例守的是"改规则要能真的管住在线的任务"，同时不许把只是需要复检的任务悄悄下架。
/// </summary>
public sealed class RiskRecheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_live_task_is_unpublished_when_the_rules_tighten_around_it()
    {
        var world = new World();
        var taskId = world.Publish("帮我把一台无人机送到郊区");
        var applicationId = world.Apply(taskId);

        world.Tighten("把无人机作业列为禁止类别", DroneRule);

        var result = world.Enforcement.Recheck();

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Unpublished);
        Assert.Equal(0, result.Frozen);

        var task = world.Tasks.Get(taskId)!;
        Assert.Equal("Cancelled", task.Status.ToString());
        Assert.Contains("prohibited.no_drones", task.CancellationReason);
        Assert.Equal("Suspended", task.RiskEnforcementStatus.ToString());

        // 平台自己的动作同样留痕：审计的操作人是固定的系统身份，不是某个运营。
        var audit = Assert.Single(world.Audits.List(10), item => item.Action == RiskEnforcementService.UnpublishAction);
        Assert.Equal(AdminAuditEntry.SystemActorId, audit.ActorId);
        Assert.Equal(taskId, audit.TargetId);

        // 所有者知道任务被下架（连同风控原因），报名过的服务者也被告知报名失效。
        Assert.Contains(world.Notifications(task.OwnerId), item => item.Type == "task.riskEnforced");
        Assert.Contains(world.Notifications(world.Worker), item => item.Type == "task.cancelled");
        Assert.NotEqual(Guid.Empty, applicationId);
    }

    [Fact]
    public void The_sweep_is_idempotent_and_leaves_tasks_on_the_current_version_alone()
    {
        var world = new World();
        world.Publish("帮我把一台无人机送到郊区");
        world.Tighten("把无人机作业列为禁止类别", DroneRule);

        Assert.Equal(1, world.Enforcement.Recheck().Scanned);
        // 判定完版本号就刷成了最新，因此第二轮不会再扫到它（这也是"重复通知"的天然屏障）。
        Assert.Equal(0, world.Enforcement.Recheck().Scanned);
        Assert.Single(world.Audits.List(10), item => item.Action == RiskEnforcementService.UnpublishAction);
    }

    [Fact]
    public void An_assigned_task_gets_its_order_frozen_instead_of_cancelled()
    {
        var world = new World();
        var taskId = world.Publish("帮我把一台无人机送到郊区");
        var applicationId = world.Apply(taskId);
        var order = world.Select(taskId, applicationId);

        world.Tighten("把无人机作业列为禁止类别", DroneRule);

        var result = world.Enforcement.Recheck();

        Assert.Equal(1, result.Frozen);
        Assert.Equal(0, result.Unpublished);

        // 任务不能直接撤销（订单还挂在服务者名下），改为冻结订单、交运营按争议流程处置。
        var task = world.Tasks.Get(taskId)!;
        Assert.Equal("Assigned", task.Status.ToString());
        Assert.Equal("Suspended", task.RiskEnforcementStatus.ToString());

        var frozen = world.Orders.Get(order.Id)!;
        Assert.Equal(OrderStatus.Disputed, frozen.Status);
        // 平台发起的冻结不指向任何一方。
        Assert.Null(frozen.DisputeOpenedBy);
        Assert.Contains("prohibited.no_drones", frozen.DisputeReason);

        var audit = Assert.Single(world.Audits.List(10), item => item.Action == RiskEnforcementService.FreezeOrderAction);
        Assert.Equal(AdminAuditEntry.SystemActorId, audit.ActorId);

        // 双方都收到"订单进入争议"，所有者另外收到风控说明。
        Assert.Contains(world.Notifications(order.OwnerId), item => item.Type == "order.disputed");
        Assert.Contains(world.Notifications(order.WorkerId), item => item.Type == "order.disputed");
        Assert.Contains(world.Notifications(order.OwnerId), item => item.Type == "task.riskEnforced");
    }

    [Fact]
    public void A_review_hit_keeps_the_task_online_and_puts_it_back_in_the_queue()
    {
        var world = new World();
        var taskId = world.Publish("帮我去实验楼取一份材料");

        world.Tighten("进入受控实验场所转人工", LaboratoryRule);

        var result = world.Enforcement.Recheck();

        Assert.Equal(1, result.Flagged);
        var task = world.Tasks.Get(taskId)!;
        // 只是"需要看一眼"：任务保持在线，但要求人工复检。
        Assert.Equal("Published", task.Status.ToString());
        Assert.Equal("RecheckRequired", task.RiskEnforcementStatus.ToString());
        Assert.Equal("Pending", task.RiskReviewStatus.ToString());
        Assert.Contains(world.Notifications(task.OwnerId), item => item.Type == "task.riskEnforced");

        // 运营复核队列里能看到它，并且带上处置状态与原因（运营据此知道这是复检、不是首次复核）。
        var queue = world.Admin.ListRiskReviews(10);
        var item = Assert.Single(queue.Items);
        Assert.Equal(taskId, item.TaskId);
        Assert.Equal("Published", item.TaskStatus);
        Assert.Equal("RecheckRequired", item.EnforcementStatus);
        Assert.Contains("受控实验场所", item.EnforcementReason);

        // 运营复核过之后，复检标记撤销（"人工看一眼"这件事已经完成了）。
        world.Admin.DecideRiskReview(taskId, "Approve", "已确认实验楼允许第三方进入", world.AdminId);
        var decided = world.Tasks.Get(taskId)!;
        Assert.Equal("None", decided.RiskEnforcementStatus.ToString());
        Assert.Equal("Approved", decided.RiskReviewStatus.ToString());
        Assert.Equal("Published", decided.Status.ToString());
    }

    [Fact]
    public void Increasing_the_reward_over_the_threshold_rejudges_a_published_task()
    {
        var world = new World();
        var taskId = world.Publish("帮我去前台取一份文件");
        Assert.Equal("Allowed", world.Tasks.Get(taskId)!.RiskVerdict.ToString());

        // 这条正是本轮修掉的绕过路径：先发一条普通任务，再把悬赏改成高价。
        var updated = world.TasksService.IncreaseReward(taskId, world.Owner, new IncreaseRewardRequest(9000));

        Assert.Equal("NeedsReview", updated.RiskVerdict);
        Assert.Equal("review.high_reward", updated.RiskRuleCode);
        Assert.Equal("Pending", updated.RiskReviewStatus);
        Assert.Equal("RecheckRequired", updated.RiskEnforcementStatus);
        Assert.Equal(9000, updated.Reward);

        // 它现在在运营队列里等着复核，所有者收到通知。
        Assert.Contains(world.Admin.ListRiskReviews(10).Items, item => item.TaskId == taskId);
        Assert.Contains(world.Notifications(world.Owner), item => item.Type == "task.riskEnforced");
    }

    [Fact]
    public void The_sweep_skips_a_task_whose_order_is_already_in_dispute()
    {
        var world = new World();
        var taskId = world.Publish("帮我把一台无人机送到郊区");
        var applicationId = world.Apply(taskId);
        var order = world.Select(taskId, applicationId);
        // 订单已经在争议里（这里是模拟"平台之前已经冻过一次"的状态）：复检不该再动它。
        world.Orders.Get(order.Id)!.SuspendByRisk("之前的处置：疑似违规", Now);

        world.Tighten("把无人机作业列为禁止类别", DroneRule);

        var result = world.Enforcement.Recheck();

        // 订单已经在争议里（已经有人在管），这一条留给下一轮，不做半截处置。
        Assert.Equal(1, result.Skipped);
        Assert.DoesNotContain(world.Audits.List(10), item => item.Action.StartsWith("task.risk.recheck", StringComparison.Ordinal));
        Assert.Equal(OrderStatus.Disputed, world.Orders.Get(order.Id)!.Status);
    }

    private static readonly RiskRuleDetailRequest DroneRule = new(
        "prohibited.no_drones", "未经许可的无人机作业", "Blocked",
        "任务涉及未经许可的无人机飞行，属于受管制活动。", ["无人机", "穿越机"]);

    private static readonly RiskRuleDetailRequest LaboratoryRule = new(
        "review.laboratory", "受控实验场所", "NeedsReview",
        "任务涉及进入受控实验场所，需要人工确认是否允许第三方进入。", ["实验楼", "实验室"]);

    private sealed class World
    {
        public World()
        {
            Clock = new MutableTimeProvider(Now);
            Owner = Guid.NewGuid();
            Worker = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Store = new InMemoryRiskRuleCatalogStore();
            Audits = new InMemoryAdminAuditRepository();
            NotificationRepository = new InMemoryNotificationRepository();
            NotificationsService = new NotificationService(NotificationRepository, Clock);

            var catalogs = new RiskRuleCatalogStoreProvider(Store);
            var unitOfWork = new InMemoryUnitOfWork();
            Tasks = new InMemoryTaskRepository();
            Orders = new InMemoryOrderRepository();

            Enforcement = new RiskEnforcementService(Tasks, Orders, Audits, NotificationsService, catalogs, Clock, unitOfWork);
            TasksService = new TaskService(Tasks, Orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(),
                new EmptyUserDirectory(), Clock, NotificationsService, unitOfWork, catalogs, Enforcement);
            Rules = new RiskRuleCatalogService(Store, Audits, new EmptyUserDirectory(), Clock, unitOfWork);
            Admin = new AdminConsoleService(Tasks, Orders, new EmptyUserDirectory(), Tasks, Orders, Audits, Clock, NotificationsService, unitOfWork);
        }

        public Guid Owner { get; }
        public Guid Worker { get; }
        public Guid AdminId { get; }
        public MutableTimeProvider Clock { get; }
        public InMemoryRiskRuleCatalogStore Store { get; }
        public InMemoryAdminAuditRepository Audits { get; }
        public InMemoryNotificationRepository NotificationRepository { get; }
        public NotificationService NotificationsService { get; }
        public InMemoryTaskRepository Tasks { get; }
        public InMemoryOrderRepository Orders { get; }
        public RiskEnforcementService Enforcement { get; }
        public TaskService TasksService { get; }
        public RiskRuleCatalogService Rules { get; }
        public AdminConsoleService Admin { get; }

        public Guid Publish(string title)
        {
            var id = TasksService.Create(new CreateTaskRequest(Owner, title, "送到指定地点", "朝阳区", Now.AddHours(6), 50, ["按时送达"])).Id;
            TasksService.Publish(id, Owner);
            return id;
        }

        public Guid Apply(Guid taskId) => TasksService
            .Apply(taskId, new ApplyForTaskRequest(Worker, "半小时可到"))
            .Applications.Single(item => item.Status == "Pending").Id;

        public OrderResponse Select(Guid taskId, Guid applicationId) =>
            TasksService.Select(taskId, applicationId, new SelectApplicationRequest(Owner)).Order;

        /// <summary>保存一版新目录：在现有规则之上再加一条（版本号由服务层自动 +1）。</summary>
        public RiskRuleCatalogDetailResponse Tighten(string reason, RiskRuleDetailRequest extraRule)
        {
            var current = Rules.GetDetail();
            return Rules.Update(
                new UpdateRiskRuleCatalogRequest(
                    current.Version, reason, current.HighRewardThreshold, current.NightWindowStart, current.NightWindowEnd,
                    [.. current.Rules.Select(rule => new RiskRuleDetailRequest(rule.Code, rule.Category, rule.Verdict, rule.Description, rule.Keywords)), extraRule]),
                AdminId);
        }

        public IReadOnlyCollection<AIToHuman.Contracts.Notifications.NotificationResponse> Notifications(Guid userId) =>
            NotificationsService.List(userId, 20).Items;
    }
}
