using AIToHuman.Application.Admin;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 误拦申诉的用例层行为：所有者能申诉、运营能处置、结论写审计并通知所有者，
/// 以及"禁止类别不因申诉放行"这条安全边界。
/// </summary>
public sealed class RiskAppealTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_owner_can_appeal_a_rejected_review_and_ops_can_accept_it()
    {
        var world = new World();
        var draft = world.Create("帮我把营业执照原件送到银行");
        world.Admin.DecideRiskReview(draft.Id, "Reject", "无法核实执照用途", world.AdminId);

        var appealed = world.Service.OpenRiskAppeal(draft.Id, world.Owner, "是我自己公司的执照，可以补充授权说明");

        Assert.Equal("Pending", appealed.RiskAppealStatus);
        Assert.Equal("是我自己公司的执照，可以补充授权说明", appealed.RiskAppealReason);
        Assert.True(appealed.CanAppealRisk == false); // 已经有待处置的申诉，不能再提一条
        Assert.Single(world.Appeals.ListAppeals(20).Items);

        var decided = world.Appeals.DecideAppeal(draft.Id, accepted: true, "已核对授权说明，属于误判", world.AdminId);

        Assert.Equal("Accepted", decided.AppealStatus);
        Assert.True(decided.CanBeReleasedByAppeal);
        Assert.Empty(world.Appeals.ListAppeals(20).Items);
        Assert.Equal("Published", world.Service.Publish(draft.Id, world.Owner).Status);

        // 结论进运营审计，并通知所有者。
        var audit = Assert.Single(world.Admin.ListAudits(20), item => item.Action == "task.risk.appeal.accept");
        Assert.Equal("已核对授权说明，属于误判", audit.Reason);
        var notification = Assert.Single(world.Inbox(world.Owner), item => item.Type == NotificationTypes.TaskRiskAppealDecided);
        Assert.True(notification.Payload.GetProperty("canPublish").GetBoolean());
        Assert.Equal("Accepted", notification.Payload.GetProperty("appealStatus").GetString());
    }

    [Fact]
    public void A_denied_appeal_keeps_the_task_locked_and_tells_the_owner()
    {
        var world = new World();
        var draft = world.Create("帮我把营业执照原件送到银行");
        world.Admin.DecideRiskReview(draft.Id, "Reject", "无法核实执照用途", world.AdminId);
        world.Service.OpenRiskAppeal(draft.Id, world.Owner, "我觉得没问题");

        world.Appeals.DecideAppeal(draft.Id, accepted: false, "材料用途仍无法核实", world.AdminId);

        Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
        var notification = Assert.Single(world.Inbox(world.Owner), item => item.Type == NotificationTypes.TaskRiskAppealDecided);
        Assert.False(notification.Payload.GetProperty("canPublish").GetBoolean());
        Assert.Equal("材料用途仍无法核实", notification.Payload.GetProperty("decisionNote").GetString());
    }

    [Fact]
    public void A_blocked_task_stays_blocked_even_when_the_appeal_is_accepted()
    {
        var world = new World();
        var draft = world.Create("帮我代考英语四级");
        world.Service.OpenRiskAppeal(draft.Id, world.Owner, "标题是引用别人的例子");

        var decided = world.Appeals.DecideAppeal(draft.Id, accepted: true, "确认属于规则误伤，已记录用于改进词表", world.AdminId);

        // 结论记下来了，但任务依旧不能发布——禁止类别人工无权放行。
        Assert.Equal("Accepted", decided.AppealStatus);
        Assert.False(decided.CanBeReleasedByAppeal);
        var error = Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
        Assert.Contains("平台禁止的类别", error.Message);
        var notification = Assert.Single(world.Inbox(world.Owner), item => item.Type == NotificationTypes.TaskRiskAppealDecided);
        Assert.False(notification.Payload.GetProperty("canPublish").GetBoolean());
    }

    [Fact]
    public void The_appeal_queue_is_only_for_tasks_with_a_pending_appeal()
    {
        var world = new World();
        var rejected = world.Create("帮我把营业执照原件送到银行");
        world.Admin.DecideRiskReview(rejected.Id, "Reject", "无法核实执照用途", world.AdminId);
        world.Create("帮我代考英语四级");

        // 还没有人提交申诉：队列是空的（禁止类别也不会自动进申诉队列）。
        Assert.Empty(world.Appeals.ListAppeals(20).Items);

        world.Service.OpenRiskAppeal(rejected.Id, world.Owner, "误判了");
        var queued = Assert.Single(world.Appeals.ListAppeals(20).Items);
        Assert.Equal(rejected.Id, queued.TaskId);
        Assert.Equal("review.identity_documents", queued.RuleCode);
    }

    [Fact]
    public void Only_the_owner_can_appeal_and_only_a_pending_appeal_can_be_decided()
    {
        var world = new World();
        var draft = world.Create("帮我代考英语四级");

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.OpenRiskAppeal(draft.Id, Guid.NewGuid(), "我来申诉"));
        Assert.Throws<DomainException>(() => world.Appeals.DecideAppeal(draft.Id, true, "放行", world.AdminId));

        world.Service.OpenRiskAppeal(draft.Id, world.Owner, "误判了");
        Assert.Throws<DomainException>(() => world.Appeals.DecideAppeal(draft.Id, true, "  ", world.AdminId));
        Assert.Empty(world.Admin.ListAudits(20));
    }

    [Fact]
    public void Editing_after_an_accepted_appeal_sends_the_task_back_to_review()
    {
        var world = new World();
        var draft = world.Create("帮我把营业执照原件送到银行");
        world.Admin.DecideRiskReview(draft.Id, "Reject", "无法核实执照用途", world.AdminId);
        world.Service.OpenRiskAppeal(draft.Id, world.Owner, "误判了");
        world.Appeals.DecideAppeal(draft.Id, accepted: true, "确认误判", world.AdminId);

        var edited = world.Service.UpdateDraft(draft.Id, world.Owner, world.DraftUpdateRequest("帮我把营业执照原件送到新地址", district: "浦东新区"));

        Assert.Equal("None", edited.RiskAppealStatus);
        Assert.Equal("Pending", edited.RiskReviewStatus);
        Assert.True(edited.RiskPublishBlocked);
        Assert.Equal(draft.Id, Assert.Single(world.Admin.ListRiskReviews(20).Items).TaskId);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();
        // 运营后台与申诉处置必须共用同一份审计仓储，否则两边各写各的、互相看不见。
        private readonly InMemoryAdminAuditRepository audits = new();

        public World()
        {
            Owner = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Service = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(), new EmptyUserDirectory(), Clock, Notifications, new InMemoryUnitOfWork());
            Admin = new AdminConsoleService(tasks, orders, new EmptyUserDirectory(), tasks, orders, audits, Clock, Notifications, new InMemoryUnitOfWork());
            Appeals = new RiskAppealService(tasks, audits, new EmptyUserDirectory(), Clock, Notifications, new InMemoryUnitOfWork());
        }

        public Guid Owner { get; }
        public Guid AdminId { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }
        public AdminConsoleService Admin { get; }
        public RiskAppealService Appeals { get; }

        public IReadOnlyCollection<NotificationResponse> Inbox(Guid userId) => Notifications.List(userId, 20).Items;

        public TaskResponse Create(string title) =>
            Service.Create(new CreateTaskRequest(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));

        public UpdateTaskDraftRequest DraftUpdateRequest(string title, string district = "朝阳区") =>
            new(Owner, title, "到前台取件", district, Now.AddHours(6), 50, ["按时送达"]);
    }
}
