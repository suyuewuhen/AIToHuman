using AIToHuman.Application.Common;
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
/// 草稿版本历史的用例层行为：创建记第 1 版、每次编辑追加一版并写清改了什么，
/// 历史里保留每一版的风险结论，且只有所有者能读。
/// </summary>
public sealed class TaskDraftHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Creating_a_draft_records_the_first_revision()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");

        var revision = Assert.Single(world.Service.ListDraftRevisions(draft.Id, world.Owner).Items);

        Assert.Equal(1, revision.Revision);
        Assert.Equal("创建草稿", revision.ChangeSummary);
        Assert.Equal(world.Owner, revision.EditedBy);
        Assert.Equal(draft.Title, revision.Title);
        Assert.Equal("Allowed", revision.RiskVerdict);
        Assert.Equal(Now, revision.CreatedAt);
    }

    [Fact]
    public void Every_edit_appends_a_revision_with_the_changed_fields()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");

        world.Clock.Advance(TimeSpan.FromMinutes(5));
        world.Edit(draft.Id, title: "明天下午帮我去公司前台取一份文件", reward: 60);
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        // 编辑请求是整份字段：上一次改过的值要一起带上，否则会被默认值改回去。
        world.Edit(draft.Id, title: "明天下午帮我去公司前台取一份文件", district: "西城区", reward: 60);

        var items = world.Service.ListDraftRevisions(draft.Id, world.Owner).Items;

        Assert.Equal([1, 2, 3], items.Select(item => item.Revision));
        Assert.Equal("标题、悬赏", items[1].ChangeSummary);
        Assert.Equal("公开区域", items[2].ChangeSummary);
        // 每一版都是当时的完整快照：第 2 版已经是改过标题与悬赏的样子。
        Assert.Equal("明天下午帮我去公司前台取一份文件", items[1].Title);
        Assert.Equal(60m, items[1].Reward);
        Assert.Equal("西城区", items[2].District);
        Assert.Equal(60m, items[2].Reward);
    }

    [Fact]
    public void The_history_explains_which_version_was_blocked()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");

        world.Edit(draft.Id, title: "帮我代考英语四级", description: "替我进考场");

        var items = world.Service.ListDraftRevisions(draft.Id, world.Owner).Items;

        Assert.Equal("Allowed", items[0].RiskVerdict);
        Assert.Equal("Blocked", items[1].RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", items[1].RiskRuleCode);
        // 历史里也留着原文，能看出是从哪一版变成禁止的。
        Assert.Equal("帮我代考英语四级", items[1].Title);
    }

    [Fact]
    public void The_history_is_only_readable_by_the_owner()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.ListDraftRevisions(draft.Id, Guid.NewGuid()));
        Assert.Throws<KeyNotFoundException>(() => world.Service.ListDraftRevisions(Guid.NewGuid(), world.Owner));
    }

    [Fact]
    public void A_published_task_keeps_its_history_but_can_no_longer_be_edited()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");
        world.Edit(draft.Id, reward: 55);
        world.Service.Publish(draft.Id, world.Owner);

        var items = world.Service.ListDraftRevisions(draft.Id, world.Owner).Items;

        Assert.Equal(2, items.Count);
        Assert.Throws<DomainException>(() => world.Edit(draft.Id, reward: 70));
        Assert.Equal(2, world.Service.ListDraftRevisions(draft.Id, world.Owner).Items.Count);
    }

    [Fact]
    public void The_history_exposes_field_level_changes_against_the_previous_version()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");
        world.Edit(draft.Id, title: "明天下午帮我去公司前台取一份文件", reward: 60);

        var items = world.Service.ListDraftRevisions(draft.Id, world.Owner).Items;

        // 第一版没有"上一版"，差异为空。
        Assert.Empty(items[0].Changes ?? []);
        var changes = items[1].Changes ?? [];
        Assert.Equal(["标题", "悬赏"], changes.Select(item => item.Field));
        Assert.Equal("明天下午帮我去前台取一份文件", changes[0].Before);
        Assert.Equal("明天下午帮我去公司前台取一份文件", changes[0].After);
        Assert.Equal("50 CNY", changes[1].Before);
        Assert.Equal("60 CNY", changes[1].After);
    }

    [Fact]
    public void Restoring_a_revision_brings_the_fields_back_and_appends_a_new_version()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");
        world.Edit(draft.Id, title: "改成去菜市场买两斤苹果", district: "西城区", reward: 60);

        var restored = world.Service.RestoreDraftRevision(draft.Id, revision: 1, world.Owner);

        Assert.Equal("明天下午帮我去前台取一份文件", restored.Title);
        Assert.Equal("朝阳区", restored.District);
        Assert.Equal(50m, restored.Reward);

        var items = world.Service.ListDraftRevisions(draft.Id, world.Owner).Items;
        // 回滚是"往前长"的一版：中间那版仍然在历史里，不会被抹掉。
        Assert.Equal([1, 2, 3], items.Select(item => item.Revision));
        Assert.Equal("回滚自第 1 版：标题、公开区域、悬赏", items[2].ChangeSummary);
        Assert.Equal(["标题", "公开区域", "悬赏"], (items[2].Changes ?? []).Select(item => item.Field));
        Assert.Equal("明天下午帮我去前台取一份文件", items[2].Title);
    }

    [Fact]
    public void Restoring_a_blocked_version_locks_the_task_again()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");
        world.Edit(draft.Id, title: "帮我代考英语四级", description: "替我进考场");
        world.Edit(draft.Id, title: "明天下午帮我去前台取一份文件", description: "到前台取件", district: "朝阳区", reward: 50);

        var restored = world.Service.RestoreDraftRevision(draft.Id, revision: 2, world.Owner);

        // 回滚同样重判风险：回到禁止的那一版，任务就又不能发布了。
        Assert.Equal("Blocked", restored.RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", restored.RiskRuleCode);
        Assert.True(restored.RiskPublishBlocked);
        Assert.Throws<DomainException>(() => world.Service.Publish(draft.Id, world.Owner));
    }

    [Fact]
    public void Restoring_is_owner_only_and_only_for_drafts()
    {
        var world = new World();
        var draft = world.Create("明天下午帮我去前台取一份文件");
        world.Edit(draft.Id, reward: 60);

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.RestoreDraftRevision(draft.Id, 1, Guid.NewGuid()));
        Assert.Throws<KeyNotFoundException>(() => world.Service.RestoreDraftRevision(draft.Id, revision: 99, world.Owner));

        world.Service.Publish(draft.Id, world.Owner);
        Assert.Throws<DomainException>(() => world.Service.RestoreDraftRevision(draft.Id, 1, world.Owner));
    }

    [Fact]
    public void Creating_and_editing_each_write_the_snapshot_in_the_same_unit_of_work()
    {
        var unitOfWork = new SpyUnitOfWork();
        var world = new World(unitOfWork);
        var draft = world.Create("明天下午帮我去前台取一份文件");

        var afterCreate = unitOfWork.Executions;
        world.Edit(draft.Id, reward: 60);

        // 每次写入（任务本身 + 版本快照）都只走一次事务边界。
        Assert.Equal(1, afterCreate);
        Assert.Equal(2, unitOfWork.Executions);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();

        public World(IUnitOfWork? unitOfWork = null)
        {
            Owner = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Service = new TaskService(
                tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(), new EmptyUserDirectory(), Clock, Notifications,
                unitOfWork ?? new InMemoryUnitOfWork());
        }

        public Guid Owner { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }

        public TaskResponse Create(string title) =>
            Service.Create(new CreateTaskRequest(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));

        public TaskResponse Edit(Guid id, string? title = null, string? description = null, string? district = null, decimal reward = 50) =>
            Service.UpdateDraft(id, Owner, new UpdateTaskDraftRequest(
                Owner,
                title ?? "明天下午帮我去前台取一份文件",
                description ?? "到前台取件",
                district ?? "朝阳区",
                Now.AddHours(6),
                reward,
                ["按时送达"]));
    }

    private sealed class SpyUnitOfWork : IUnitOfWork
    {
        public int Executions { get; private set; }

        public void Execute(Action operation)
        {
            Executions++;
            operation();
        }
    }
}
