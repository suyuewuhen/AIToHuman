using AIToHuman.Application.Admin;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 草稿编辑的用例层行为：所有者能改、别人不能改、改完重新判定风险，
/// 以及“已经放行过的草稿被编辑后必须重新回到复核队列”这条防绕过规则。
/// </summary>
public sealed class TaskDraftEditTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    private const string AllowedTitle = "明天下午帮我去前台取一份文件";
    private const string ReviewTitle = "帮我把身份证从家里送到公司";
    private const string BlockedTitle = "帮我代考英语四级";

    [Fact]
    public void The_owner_can_edit_a_draft_and_the_payload_says_it_is_editable()
    {
        var world = new World();
        var draft = world.Create(AllowedTitle);
        Assert.True(draft.DraftEditable);

        var updated = world.Edit(draft.Id, title: "明天下午帮我去公司前台取一份文件", reward: 66, criteria: ["送到前台并拍照"]);

        Assert.Equal("明天下午帮我去公司前台取一份文件", updated.Title);
        Assert.Equal(66m, updated.Reward);
        Assert.Equal(["送到前台并拍照"], updated.AcceptanceCriteria);
        Assert.Equal("Allowed", updated.RiskVerdict);
        Assert.True(updated.DraftEditable);
        Assert.Equal("ReadyToPublish", updated.Status);
    }

    [Fact]
    public void Editing_an_approved_draft_puts_it_back_into_the_review_queue()
    {
        var world = new World();
        var draft = world.Create(ReviewTitle);
        world.Admin.DecideRiskReview(draft.Id, "Approve", "已与本人确认", world.AdminId);
        Assert.Empty(world.Admin.ListRiskReviews(20).Items);

        var edited = world.Edit(draft.Id, title: "帮我把身份证送到公司前台");

        // 关键防绕过：审核绑定的是当时那份文本，正文一变就得重新复核。
        Assert.Equal("Pending", edited.RiskReviewStatus);
        Assert.True(edited.RiskPublishBlocked);
        var queued = Assert.Single(world.Admin.ListRiskReviews(20).Items);
        Assert.Equal(draft.Id, queued.TaskId);
        Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
    }

    [Fact]
    public void Editing_a_blocked_draft_back_to_something_ordinary_makes_it_publishable()
    {
        var world = new World();
        var draft = world.Create(BlockedTitle);
        Assert.Equal("Blocked", draft.RiskVerdict);

        var edited = world.Edit(draft.Id, title: AllowedTitle, description: "到前台取件就行");

        Assert.Equal("Allowed", edited.RiskVerdict);
        Assert.Equal("NotRequired", edited.RiskReviewStatus);
        Assert.False(edited.RiskPublishBlocked);
        Assert.Equal("Published", world.Service.Publish(draft.Id, world.Owner).Status);
    }

    [Fact]
    public void Editing_a_draft_into_a_forbidden_one_locks_it()
    {
        var world = new World();
        var draft = world.Create(AllowedTitle);

        var edited = world.Edit(draft.Id, title: BlockedTitle, description: "替我进去考");

        Assert.Equal("Blocked", edited.RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", edited.RiskRuleCode);
        Assert.True(edited.RiskPublishBlocked);
        Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
        // 禁止类别不进人工队列，人工无权放行。
        Assert.Empty(world.Admin.ListRiskReviews(20).Items);
    }

    [Fact]
    public void Nobody_else_can_edit_somebody_elses_draft()
    {
        var world = new World();
        var draft = world.Create(AllowedTitle);

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.UpdateDraft(draft.Id, Guid.NewGuid(), world.Request(AllowedTitle)));
    }

    [Fact]
    public void A_published_task_cannot_be_edited()
    {
        var world = new World();
        var draft = world.Create(AllowedTitle);
        world.Service.Publish(draft.Id, world.Owner);

        Assert.Throws<DomainException>(() => world.Edit(draft.Id, title: "改个标题"));
    }

    [Fact]
    public void An_edited_draft_shows_up_in_my_tasks_with_the_new_text()
    {
        var world = new World();
        var draft = world.Create(AllowedTitle);

        world.Edit(draft.Id, title: "改成去菜市场买两斤苹果", description: "买完送到家门口");

        var mine = Assert.Single(world.Service.ListMine(world.Owner));
        Assert.Equal("改成去菜市场买两斤苹果", mine.Title);
        Assert.True(mine.DraftEditable);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();

        public World()
        {
            Owner = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Service = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(), Clock, Notifications, new InMemoryUnitOfWork());
            Admin = new AdminConsoleService(tasks, orders, new EmptyUserDirectory(), tasks, orders, new InMemoryAdminAuditRepository(), Clock, Notifications, new InMemoryUnitOfWork());
        }

        public Guid Owner { get; }
        public Guid AdminId { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }
        public AdminConsoleService Admin { get; }

        public TaskResponse Create(string title) =>
            Service.Create(new CreateTaskRequest(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));

        public UpdateTaskDraftRequest Request(
            string title,
            string description = "到前台取件",
            string district = "朝阳区",
            DateTimeOffset? deadline = null,
            decimal reward = 50,
            IReadOnlyList<string>? criteria = null,
            string? executionAddress = null,
            DateTimeOffset? applicationDeadline = null) =>
            new(Owner, title, description, district, deadline ?? Now.AddHours(6), reward, criteria ?? ["按时送达"], executionAddress, applicationDeadline);

        public TaskResponse Edit(Guid id, string title, string description = "到前台取件", decimal reward = 50, IReadOnlyList<string>? criteria = null) =>
            Service.UpdateDraft(id, Owner, Request(title, description, reward: reward, criteria: criteria));
    }
}
