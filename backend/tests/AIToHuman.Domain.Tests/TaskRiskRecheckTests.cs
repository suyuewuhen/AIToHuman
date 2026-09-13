using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确指向领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 发布后风险复检的领域规则：规则改了、悬赏加了之后，一条**已经在线**的任务该怎么处置。
/// 这一组用例守的是三条边界：禁止类别当场处置、有订单不硬撤（交订单去冻结）、
/// 只是"需要看一眼"时任务保持在线（不能因为规则升级就把大厅里的任务批量下架）。
/// </summary>
public sealed class TaskRiskRecheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Worker = Guid.NewGuid();

    [Fact]
    public void A_published_task_is_unpublished_when_the_new_rules_block_it()
    {
        var task = Published("帮我把一台无人机送到郊区");
        Assert.Equal(RiskVerdict.Allowed, task.RiskVerdict);

        var outcome = task.ReassessRisk(BlockingDrones(), Now.AddHours(1));

        Assert.Equal(RiskEnforcementOutcome.Unpublished, outcome);
        // 没有订单：当场下架，理由里写清是被哪条规则、哪一版拦的。
        Assert.Equal(TaskStatus.Cancelled, task.Status);
        Assert.NotNull(task.CancelledAt);
        Assert.Contains("prohibited.no_drones", task.CancellationReason);
        Assert.Equal(RiskEnforcementStatus.Suspended, task.RiskEnforcementStatus);
        Assert.Contains("prohibited.no_drones", task.RiskEnforcementReason);
        Assert.Equal(Now.AddHours(1), task.RiskEnforcedAt);
        Assert.Equal(2, task.RiskRuleVersion);
    }

    [Fact]
    public void An_assigned_task_is_not_cancelled_but_asks_for_an_order_freeze()
    {
        // 已经有订单：任务不能直接撤销（否则订单还挂在服务者名下），要由用例层去冻结订单。
        var task = Assigned("帮我把一台无人机送到郊区");

        var outcome = task.ReassessRisk(BlockingDrones(), Now.AddHours(1));

        Assert.Equal(RiskEnforcementOutcome.OrderFrozen, outcome);
        Assert.Equal(TaskStatus.Assigned, task.Status);
        Assert.Equal(RiskEnforcementStatus.Suspended, task.RiskEnforcementStatus);
        Assert.Null(task.CancelledAt);
    }

    [Fact]
    public void A_review_verdict_keeps_the_task_online_and_asks_for_a_recheck()
    {
        var task = Published("帮我去实验楼取一份材料");

        var outcome = task.ReassessRisk(ReviewingLabs(), Now.AddHours(1));

        Assert.Equal(RiskEnforcementOutcome.FlaggedForRecheck, outcome);
        // 只是"需要看一眼"，不能把一条在线任务直接下架。
        Assert.Equal(TaskStatus.Published, task.Status);
        Assert.Equal(RiskEnforcementStatus.RecheckRequired, task.RiskEnforcementStatus);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
        Assert.Contains("受控实验场所", task.RiskEnforcementReason);
    }

    [Fact]
    public void A_task_that_is_allowed_again_has_its_recheck_flag_cleared()
    {
        var task = Published("帮我去实验楼取一份材料");
        task.ReassessRisk(ReviewingLabs(), Now.AddHours(1));

        // 规则又放开（回到内置目录）：复检要求撤销，任务继续正常在线。
        var outcome = task.ReassessRisk(RiskRuleCatalog.BuiltIn, Now.AddHours(2));

        Assert.Equal(RiskEnforcementOutcome.Unchanged, outcome);
        Assert.Equal(RiskEnforcementStatus.None, task.RiskEnforcementStatus);
        Assert.Null(task.RiskEnforcementReason);
        Assert.Null(task.RiskEnforcedAt);
        Assert.Equal(RiskReviewStatus.NotRequired, task.RiskReviewStatus);
    }

    [Fact]
    public void A_human_approval_is_not_erased_by_an_allowed_recheck()
    {
        // 先走一遍"转人工 → 运营放行 → 发布"，再用一版不再命中它的目录复检：
        // 复检只负责把结论刷新到最新规则，不能把人对某一版文本的结论抹掉。
        var task = Create("帮我把身份证送到公司");
        task.ApproveRiskReview(Guid.NewGuid(), "已核实", Now);
        task.Publish(Now);
        Assert.Equal(RiskReviewStatus.Approved, task.RiskReviewStatus);

        var outcome = task.ReassessRisk(NeutralCatalog(), Now.AddHours(1));

        Assert.Equal(RiskEnforcementOutcome.Unchanged, outcome);
        Assert.Equal(RiskReviewStatus.Approved, task.RiskReviewStatus);
        Assert.Equal(RiskEnforcementStatus.None, task.RiskEnforcementStatus);
    }

    [Fact]
    public void Reassessment_refuses_tasks_that_are_not_online()
    {
        // 草稿还没发布：判定在创建/编辑时就做过了，这里不该受理。
        var draft = Create("帮我把一台无人机送到郊区");
        Assert.Throws<DomainException>(() => draft.ReassessRisk(BlockingDrones(), Now));

        // 已撤销的任务同样不再复检（它已经不在线上了）。
        var cancelled = Published("帮我把一台无人机送到郊区");
        cancelled.Cancel("不发了", Now);
        Assert.Throws<DomainException>(() => cancelled.ReassessRisk(BlockingDrones(), Now.AddHours(1)));
    }

    [Fact]
    public void Only_online_tasks_whose_rule_version_is_stale_need_a_recheck()
    {
        var draft = Create("帮我把一台无人机送到郊区");
        var published = Published("帮我把一台无人机送到郊区");

        Assert.False(draft.NeedsRiskRecheck(2));
        Assert.True(published.NeedsRiskRecheck(2));
        // 已经是当前版本：不用再扫（这样复检扫描天然幂等）。
        Assert.False(published.NeedsRiskRecheck(RiskRuleCatalog.BuiltIn.Version));
    }

    [Fact]
    public void Increasing_the_reward_rejudges_a_published_task()
    {
        // 加价到高金额本来就该转人工：不重判的话，"先发普通任务再改成高价"就是现成的绕过路径。
        var task = Published("帮我去前台取一份文件");

        var outcome = task.IncreaseReward(new Money(9000), Now.AddMinutes(5), NeutralCatalog());

        Assert.Equal(RiskEnforcementOutcome.FlaggedForRecheck, outcome);
        Assert.Equal(9000, task.Reward.Amount);
        Assert.Equal("review.high_reward", task.RiskRuleCode);
        Assert.Equal(RiskEnforcementStatus.RecheckRequired, task.RiskEnforcementStatus);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
    }

    [Fact]
    public void Increasing_the_reward_below_the_threshold_changes_nothing()
    {
        var task = Published("帮我去前台取一份文件");

        var outcome = task.IncreaseReward(new Money(80), Now.AddMinutes(5), NeutralCatalog());

        Assert.Equal(RiskEnforcementOutcome.Unchanged, outcome);
        Assert.Equal(RiskEnforcementStatus.None, task.RiskEnforcementStatus);
        Assert.Equal(RiskReviewStatus.NotRequired, task.RiskReviewStatus);
    }

    [Fact]
    public void An_order_can_be_frozen_for_risk_but_not_when_it_is_already_settled()
    {
        var order = new Order(Guid.NewGuid(), Owner, Worker, "代取文件", new Money(50), Now);
        order.Start(Worker);
        Assert.True(order.CanBeSuspendedForRisk);

        order.SuspendByRisk("任务命中平台禁止的类别。", Now.AddHours(1));

        Assert.Equal(OrderStatus.Disputed, order.Status);
        // 平台发起的冻结不指向任何一方：运营一眼能看出这不是参与者发起的争议。
        Assert.Null(order.DisputeOpenedBy);
        Assert.Equal(Now.AddHours(1), order.DisputeOpenedAt);
        Assert.Contains("禁止", order.DisputeReason);
        // 已经在争议里就不该被再冻一次。
        Assert.False(order.CanBeSuspendedForRisk);
        Assert.Throws<DomainException>(() => order.SuspendByRisk("再来一次", Now.AddHours(2)));
    }

    [Fact]
    public void Freezing_an_order_requires_a_reason()
    {
        var order = new Order(Guid.NewGuid(), Owner, Worker, "代取文件", new Money(50), Now);

        Assert.Throws<DomainException>(() => order.SuspendByRisk("   ", Now));
    }

    [Fact]
    public void An_approved_review_is_not_requeued_when_the_same_rule_still_matches()
    {
        // 运营已经就"证件与重要文件"这条规则放行过这份文本：规则版本升级、命中的还是同一条规则时，
        // 不该把它重新排进队列——否则每改一次规则，所有含"身份证/医院/高金额"的在售任务都会再惊动一次运营与所有者。
        var task = Create("帮我把身份证送到公司");
        task.ApproveRiskReview(Guid.NewGuid(), "已核实用途", Now);
        task.Publish(Now);

        var outcome = task.ReassessRisk(
            Catalog(new RiskRule("review.identity_documents", "证件与重要文件", RiskVerdict.NeedsReview,
                "任务涉及他人证件或重要文件的交接，需要人工确认授权与用途。", ["身份证"])),
            Now.AddHours(1));

        Assert.Equal(RiskEnforcementOutcome.Unchanged, outcome);
        Assert.Equal(RiskReviewStatus.Approved, task.RiskReviewStatus);
        Assert.Equal(RiskEnforcementStatus.None, task.RiskEnforcementStatus);
        // 版本号照常刷新：这条任务确实已经按新一版规则看过一遍了。
        Assert.Equal(2, task.RiskRuleVersion);
    }

    [Fact]
    public void A_task_already_waiting_for_review_is_not_flagged_twice()
    {
        var task = Published("帮我去实验楼取一份材料");
        Assert.Equal(RiskEnforcementOutcome.FlaggedForRecheck, task.ReassessRisk(ReviewingLabs(), Now.AddHours(1)));
        var reasonAfterFirstFlag = task.RiskEnforcementReason;

        // 又一次规则升级（仍然是同一条规则命中）：已经排队等着了，只刷新版本号。
        var outcome = task.ReassessRisk(ReviewingLabs(version: 3), Now.AddHours(2));

        Assert.Equal(RiskEnforcementOutcome.Unchanged, outcome);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
        Assert.Equal(RiskEnforcementStatus.RecheckRequired, task.RiskEnforcementStatus);
        Assert.Equal(reasonAfterFirstFlag, task.RiskEnforcementReason);
        Assert.Equal(3, task.RiskRuleVersion);
    }

    private static TaskItem Create(string title) =>
        new(Owner, title, "送到指定地点", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now);

    private static TaskItem Published(string title)
    {
        var task = Create(title);
        task.Publish(Now);
        return task;
    }

    private static TaskItem Assigned(string title)
    {
        var task = Create(title);
        task.Publish(Now);
        var application = task.Apply(Worker, "半小时可到", Now);
        task.SelectApplication(application.Id);
        return task;
    }

    /// <summary>第 2 版目录：把"无人机"列为禁止类别。</summary>
    private static RiskRuleCatalog BlockingDrones() => Catalog(
        new RiskRule("prohibited.no_drones", "未经许可的无人机作业", RiskVerdict.Blocked,
            "任务涉及未经许可的无人机飞行，属于受管制活动。", ["无人机", "穿越机"]));

    /// <summary>第 2 版目录：进入实验场所转人工（用来验证"只要求复检、不下架"）。</summary>
    private static RiskRuleCatalog ReviewingLabs(int version = 2) => Catalog(
        new RiskRule("review.laboratory", "受控实验场所", RiskVerdict.NeedsReview,
            "任务涉及进入受控实验场所，需要人工确认是否允许第三方进入。", ["实验楼", "实验室"]),
        version);

    /// <summary>
    /// 第 2 版目录：只有一条占位禁止类规则（阈值与内置一致）。
    /// 用来验证"这一版不再命中某条任务"时的复检行为，以及加价到 5000 元以上会转人工。
    /// </summary>
    private static RiskRuleCatalog NeutralCatalog() => new(
        2,
        [new RiskRule("prohibited.placeholder", "占位禁止类", RiskVerdict.Blocked, "目录必须至少有一条禁止类规则。", ["代考"])],
        5000m,
        RiskRuleCatalog.BuiltIn.NightWindowStart,
        RiskRuleCatalog.BuiltIn.NightWindowEnd);

    private static RiskRuleCatalog Catalog(RiskRule rule, int version = 2) => new(
        version,
        [new RiskRule("prohibited.placeholder", "占位禁止类", RiskVerdict.Blocked, "目录必须至少有一条禁止类规则。", ["代考"]), rule],
        5000m,
        RiskRuleCatalog.BuiltIn.NightWindowStart,
        RiskRuleCatalog.BuiltIn.NightWindowEnd);
}
