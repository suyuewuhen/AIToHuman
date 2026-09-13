using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Payments;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Payments;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Risk;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 判定留痕与命中统计的用例层行为：每个会跑判定的动作都要留下一条记录，
/// 统计要能回答"哪条规则拦了多少、其中多少被判成误伤"。
/// </summary>
public sealed class RiskDecisionStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Every_assessment_point_writes_exactly_one_entry()
    {
        var world = new World();

        // 创建（判定一次）→ 编辑（再判定一次）：两次都要留痕，且原因可区分。
        var taskId = world.Create("帮我取一份文件");
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Edit(taskId, "帮我取一份材料");
        var afterEdit = world.Decisions.ListByTask(taskId, 20).ToArray();
        Assert.Equal(2, afterEdit.Length);
        Assert.Equal(RiskDecisionReason.Created, afterEdit[0].Reason);
        Assert.Equal(RiskDecisionReason.Edited, afterEdit[1].Reason);

        // 回滚到第 1 版（第三次判定）：只有草稿能回滚，所以这一步必须在发布之前。
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Tasks.RestoreDraftRevision(taskId, 1, world.Owner);
        Assert.Equal(RiskDecisionReason.Restored, world.Decisions.ListByTask(taskId, 20).ElementAt(2).Reason);

        // 发布（第四次判定）：这一关也要有记录，否则"发布拦了多少"查不出来。
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Tasks.Publish(taskId, world.Owner);
        var afterPublish = world.Decisions.ListByTask(taskId, 20).ToArray();
        Assert.Equal(4, afterPublish.Length);
        Assert.Equal(RiskDecisionReason.Published, afterPublish[3].Reason);
    }

    [Fact]
    public void Raising_the_reward_and_rechecking_are_recorded_separately()
    {
        var world = new World();
        var taskId = world.Publish("帮我去前台取一份文件");

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Tasks.IncreaseReward(taskId, world.Owner, new IncreaseRewardRequest(9000));
        var afterRaise = world.Decisions.ListByTask(taskId, 20).ToArray();
        Assert.Equal(RiskDecisionReason.RewardRaised, afterRaise[^1].Reason);
        Assert.Equal(RiskVerdict.NeedsReview, afterRaise[^1].Verdict);
        Assert.Equal("review.high_reward", afterRaise[^1].RuleCode);

        // 规则升级后的复检也算一次判定（reason 是 Rechecked，统计里单独计数）。
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Tighten("把无人机作业列为禁止类别");
        var result = world.Enforcement.Recheck();
        Assert.True(result.Scanned >= 1);
        Assert.Contains(world.Decisions.ListByTask(taskId, 20), item => item.Reason == RiskDecisionReason.Rechecked);
    }

    [Fact]
    public void The_summary_counts_hits_per_rule_and_the_appeals_that_were_accepted()
    {
        var world = new World();

        // 三条被"代考"拦下的任务 + 一条正常任务（放行也要计入总数）。
        world.Create("帮我代考英语四级 1");
        world.Create("帮我代考英语四级 2");
        var appealed = world.Create("帮我代考英语四级 3");
        world.PublishAllowed("帮我去前台取一份文件");

        // 其中一条申诉被认定为误伤：统计里要能看到"拦了 3 次、1 次被判成误伤"。
        world.Tasks.OpenRiskAppeal(appealed, world.Owner, "其实只是帮家里人跑腿");
        world.Appeals.DecideAppeal(appealed, accepted: true, "确认是误判", world.AdminId);

        var stats = world.RiskDecisions.Summarize(30, Now.AddHours(1));

        Assert.Equal(5, stats.TotalDecisions);
        Assert.Equal(2, stats.AllowedCount);
        Assert.Equal(3, stats.BlockedCount);
        Assert.Equal(0, stats.NeedsReviewCount);

        var rule = Assert.Single(stats.Rules, item => item.Code == "prohibited.exam_impersonation");
        Assert.Equal(3, rule.Hits);
        Assert.Equal("Blocked", rule.Verdict);
        Assert.Equal(1, rule.AcceptedAppeals);
        Assert.Equal("代考与冒名顶替", rule.Category);
        Assert.True(rule.FirstHitAt <= rule.LastHitAt);
    }

    [Fact]
    public void The_summary_window_excludes_older_decisions()
    {
        var world = new World();
        world.Create("帮我代考英语四级");

        // 窗口 1 天：把"现在"往前推 10 天，这条判定就落在窗口外了。
        var stats = world.RiskDecisions.Summarize(1, Now.AddDays(10));

        Assert.Equal(0, stats.TotalDecisions);
        Assert.Empty(stats.Rules);
        Assert.Equal(1, stats.WindowDays);
    }

    [Fact]
    public void The_summary_counts_rechec_decisions_separately()
    {
        var world = new World();
        world.Publish("帮我把一台无人机送到郊区");
        world.Tighten("把无人机作业列为禁止类别");
        world.Enforcement.Recheck();

        var stats = world.RiskDecisions.Summarize(30, Now.AddHours(1));

        var rule = Assert.Single(stats.Rules, item => item.Code == "prohibited.no_drones");
        Assert.Equal(1, rule.Hits);
        // 这条命中是复检产生的，要在明细里能分辨出来。
        Assert.Equal(1, rule.RecheckedHits);
        Assert.Equal(1, stats.RecheckedCount);
        Assert.Equal(1, stats.BlockedCount);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();
        private readonly InMemoryAdminAuditRepository audits = new();
        private readonly InMemoryUnitOfWork unitOfWork = new();
        private readonly InMemoryNotificationRepository notifications = new();
        private readonly InMemoryRiskRuleCatalogStore catalogs = new();
        private readonly InMemoryRiskAppealRepository appeals = new();

        public World()
        {
            Owner = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Decisions = new InMemoryRiskDecisionRepository();
            Stats = new RiskDecisionService(Decisions, appeals);
            var catalogsProvider = new RiskRuleCatalogStoreProvider(catalogs);
            var notificationsService = new NotificationService(notifications, Clock);

            Enforcement = new RiskEnforcementService(tasks, orders, audits, notificationsService, catalogsProvider, Clock, unitOfWork, Stats);
            Tasks = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(),
                new EmptyUserDirectory(), Clock, notificationsService, unitOfWork,
                riskRuleCatalog: catalogsProvider, riskEnforcement: Enforcement, riskAppeals: appeals, riskDecisions: Stats);
            Appeals = new RiskAppealService(tasks, appeals, audits, new EmptyUserDirectory(), Clock, notificationsService, unitOfWork);
        }

        public Guid Owner { get; }
        public Guid AdminId { get; }
        public MutableTimeProvider Clock { get; }
        public InMemoryRiskDecisionRepository Decisions { get; }
        public RiskDecisionService Stats { get; }
        public RiskDecisionService RiskDecisions => Stats;
        public TaskService Tasks { get; }
        public RiskAppealService Appeals { get; }
        public RiskEnforcementService Enforcement { get; }

        public Guid Create(string title) => Tasks.Create(new CreateTaskRequest(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"])).Id;

        public Guid Publish(string title)
        {
            var id = Create(title);
            Tasks.Publish(id, Owner);
            return id;
        }

        public void PublishAllowed(string title) => Publish(title);

        public void Edit(Guid taskId, string title) => Tasks.UpdateDraft(taskId, Owner,
            new UpdateTaskDraftRequest(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), 50, ["按时送达"]));

        /// <summary>保存一版新目录（在现有规则之上加一条禁止类规则）。</summary>
        public void Tighten(string reason)
        {
            var current = new RiskRuleCatalogService(catalogs, audits, new EmptyUserDirectory(), Clock, unitOfWork).GetDetail();
            new RiskRuleCatalogService(catalogs, audits, new EmptyUserDirectory(), Clock, unitOfWork).Update(
                new AIToHuman.Contracts.Admin.UpdateRiskRuleCatalogRequest(
                    current.Version, reason, current.HighRewardThreshold, current.NightWindowStart, current.NightWindowEnd,
                    [
                        .. current.Rules.Select(rule => new AIToHuman.Contracts.Admin.RiskRuleDetailRequest(rule.Code, rule.Category, rule.Verdict, rule.Description, rule.Keywords)),
                        new AIToHuman.Contracts.Admin.RiskRuleDetailRequest("prohibited.no_drones", "未经许可的无人机作业", "Blocked",
                            "任务涉及未经许可的无人机飞行，属于受管制活动。", ["无人机", "穿越机"])
                    ]),
                AdminId);
        }
    }
}
