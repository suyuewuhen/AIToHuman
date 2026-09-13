using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Contracts.Notifications;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 风险门禁与运营复核的用例层行为：草稿照常创建但不能发布、转人工进队列、处置后写审计并通知所有者。
/// </summary>
public sealed class TaskRiskReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    private const string AllowedTitle = "明天下午帮我去前台取一份文件";
    private const string ReviewTitle = "帮我把身份证从家里送到公司";
    private const string BlockedTitle = "帮我代考英语四级";

    [Fact]
    public void A_blocked_draft_is_created_but_stays_out_of_the_lobby_forever()
    {
        var world = new World();
        var draft = world.Create(BlockedTitle);

        Assert.Equal("Blocked", draft.RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", draft.RiskRuleCode);
        Assert.True(draft.RiskPublishBlocked);

        var error = Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
        Assert.Contains("禁止", error.Message);
        // 大厅里没有它，也没有进人工队列（人工无权放行禁止类别）。
        Assert.Empty(world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items);
        Assert.Empty(world.Admin.ListRiskReviews(20).Items);
        // 所有者仍然看得到自己的草稿与原因，可以自己撤销。
        var mine = world.Service.ListMine(world.Owner).Single();
        Assert.Equal("Blocked", mine.RiskVerdict);
        Assert.Equal("代考与冒名顶替", mine.RiskCategory);
    }

    [Fact]
    public void A_sensitive_draft_enters_the_review_queue_and_publishes_only_after_approval()
    {
        var world = new World();
        var draft = world.Create(ReviewTitle);

        Assert.Equal("NeedsReview", draft.RiskVerdict);
        Assert.Equal("Pending", draft.RiskReviewStatus);
        Assert.True(draft.RiskPublishBlocked);

        var error = Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
        Assert.Contains("人工复核", error.Message);

        var queue = world.Admin.ListRiskReviews(20).Items;
        var item = Assert.Single(queue);
        Assert.Equal(draft.Id, item.TaskId);
        Assert.Equal("review.identity_documents", item.RuleCode);
        Assert.Equal("Pending", item.ReviewStatus);
        Assert.Equal(world.Owner, item.OwnerId);
        Assert.Contains("身份证", item.Title);

        // 运营放行：写审计、通知所有者，然后所有者才能真正发布。
        var decided = world.Admin.DecideRiskReview(draft.Id, "Approve", "已与本人确认是自用证件", world.AdminId);
        Assert.Equal("Approved", decided.ReviewStatus);
        Assert.Empty(world.Admin.ListRiskReviews(20).Items);

        var notification = Assert.Single(world.Inbox(world.Owner), entry => entry.Type == NotificationTypes.TaskRiskReviewed);
        Assert.True(notification.Payload.GetProperty("canPublish").GetBoolean());
        Assert.Equal("Approved", notification.Payload.GetProperty("reviewStatus").GetString());

        var published = world.Service.Publish(draft.Id, world.Owner);
        Assert.Equal("Published", published.Status);
        Assert.Single(world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items);
    }

    [Fact]
    public void A_rejected_draft_never_reaches_the_lobby_and_tells_the_owner_why()
    {
        var world = new World();
        var draft = world.Create(ReviewTitle);

        var decided = world.Admin.DecideRiskReview(draft.Id, "Reject", "无法核实证件用途", world.AdminId);
        Assert.Equal("Rejected", decided.ReviewStatus);

        var error = Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
        Assert.Contains("未通过人工复核", error.Message);
        Assert.Empty(world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items);

        var notification = Assert.Single(world.Inbox(world.Owner), entry => entry.Type == NotificationTypes.TaskRiskReviewed);
        Assert.False(notification.Payload.GetProperty("canPublish").GetBoolean());

        // 所有者仍可自己撤销这条草稿。
        var cancelled = world.Service.CancelTask(draft.Id, world.Owner, "不发了");
        Assert.Equal("Cancelled", cancelled.Status);
    }

    [Fact]
    public void Every_review_decision_needs_a_reason_and_writes_an_audit_entry()
    {
        var world = new World();
        var draft = world.Create(ReviewTitle);

        Assert.Throws<DomainException>(() => world.Admin.DecideRiskReview(draft.Id, "Approve", "  ", world.AdminId));
        Assert.Empty(world.Admin.ListAudits(20));
        Assert.Equal("Pending", world.Admin.ListRiskReviews(20).Items.Single().ReviewStatus);

        world.Admin.DecideRiskReview(draft.Id, "Approve", "已核实", world.AdminId);

        var audit = Assert.Single(world.Admin.ListAudits(20));
        Assert.Equal("task.risk.approve", audit.Action);
        Assert.Equal("task", audit.TargetType);
        Assert.Equal(draft.Id, audit.TargetId);
        Assert.Equal(world.AdminId, audit.ActorId);
        Assert.Equal("已核实", audit.Reason);
    }

    [Fact]
    public void A_review_cannot_be_decided_twice_and_unknown_decisions_are_rejected()
    {
        var world = new World();
        var draft = world.Create(ReviewTitle);
        world.Admin.DecideRiskReview(draft.Id, "Approve", "已核实", world.AdminId);

        Assert.Throws<DomainException>(() => world.Admin.DecideRiskReview(draft.Id, "Reject", "反悔", world.AdminId));
        // 取值校验发生在领域判定之前，非法取值即使任务不在队列里也要报“可选值”。
        var error = Assert.Throws<DomainException>(() => world.Admin.DecideRiskReview(draft.Id, "Maybe", "猜一下", world.AdminId));
        Assert.Contains("可选值", error.Message);
        Assert.Single(world.Admin.ListAudits(20));
    }

    [Fact]
    public void The_queue_is_first_in_first_out_and_respects_the_limit()
    {
        var world = new World();
        var first = world.Create(ReviewTitle);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = world.Create("帮我把营业执照原件送到银行");

        var firstPage = world.Admin.ListRiskReviews(1).Items;

        Assert.Equal(first.Id, Assert.Single(firstPage).TaskId);
        Assert.Equal(2, world.Admin.ListRiskReviews(20).Items.Count);
        Assert.Equal(second.Id, world.Admin.ListRiskReviews(20).Items.Last().TaskId);
    }

    [Fact]
    public void The_task_payload_carries_the_risk_state_so_the_page_can_explain_the_lock()
    {
        var world = new World();
        var draft = world.Create(ReviewTitle);

        var response = world.Service.Get(draft.Id)!;

        Assert.Equal("NeedsReview", response.RiskVerdict);
        Assert.Equal("review.identity_documents", response.RiskRuleCode);
        Assert.Equal("证件与重要文件", response.RiskCategory);
        Assert.Equal("Pending", response.RiskReviewStatus);
        Assert.True(response.RiskPublishBlocked);
        Assert.Equal(RiskRuleCatalog.Version, response.RiskRuleVersion);
        Assert.NotNull(response.RiskAssessedAt);
    }

    [Fact]
    public void An_ordinary_task_is_untouched_by_the_gate()
    {
        var world = new World();
        var task = world.Create(AllowedTitle);

        Assert.Equal("Allowed", task.RiskVerdict);
        Assert.Equal("NotRequired", task.RiskReviewStatus);
        Assert.False(task.RiskPublishBlocked);

        var published = world.Service.Publish(task.Id, world.Owner);
        Assert.Equal("Published", published.Status);
        Assert.Empty(world.Admin.ListRiskReviews(20).Items);
    }

    [Fact]
    public void The_rule_catalog_view_explains_what_is_blocked_without_leaking_match_words()
    {
        var described = AdminConsoleService.DescribeRiskRules();

        Assert.Equal(RiskRuleCatalog.Version, described.Version);
        Assert.True(described.HighRewardThreshold > 0);
        Assert.Contains(described.Rules, rule => rule.Code == "prohibited.exam_impersonation" && rule.Verdict == "Blocked");
        Assert.Contains(described.Rules, rule => rule.Code == "review.identity_documents" && rule.Verdict == "NeedsReview");
        // 只给出匹配词的数量，不给出匹配词本身：否则用户能逐字试探绕过。
        Assert.All(described.Rules, rule => Assert.True(rule.KeywordCount > 0));
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();
        private readonly InMemoryAdminAuditRepository audits = new();

        public World()
        {
            Owner = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Service = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(), new EmptyUserDirectory(), Clock, Notifications, new InMemoryUnitOfWork());
            Admin = new AdminConsoleService(tasks, orders, new EmptyUserDirectory(), tasks, orders, audits, Clock, Notifications, new InMemoryUnitOfWork());
        }

        public Guid Owner { get; }
        public Guid AdminId { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }
        public AdminConsoleService Admin { get; }

        public IReadOnlyCollection<NotificationResponse> Inbox(Guid userId) => Notifications.List(userId, 20).Items;

        public TaskResponse Create(string title) =>
            Service.Create(new CreateTaskRequest(Owner, title, "按门牌号送到前台", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));
    }
}
