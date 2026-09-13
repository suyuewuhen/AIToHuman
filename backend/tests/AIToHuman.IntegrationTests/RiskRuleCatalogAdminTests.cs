using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Risk;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 运营编辑风险规则的用例层行为：版本只追加、依据必填、乐观并发挡旧覆盖、
/// 审计留痕，以及"新版本立刻对后续判定生效"。
/// 判定本身依赖真实数据库与否不影响这些结论，所以这里用内存存储跑（真库往返另见 Postgres 回归用例）。
/// </summary>
public sealed class RiskRuleCatalogAdminTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Before_any_edit_the_effective_catalog_is_the_built_in_one()
    {
        var world = new World();

        var summary = world.Rules.GetSummary();
        var detail = world.Rules.GetDetail();
        var versions = world.Rules.ListVersions(10);

        Assert.Equal(RiskRuleCatalog.BuiltInVersion, summary.Version);
        Assert.Equal(RiskRuleCatalog.BuiltInVersion, detail.Version);
        Assert.True(detail.IsBuiltIn);
        Assert.Null(detail.UpdatedBy);
        Assert.Contains(detail.Rules, rule => rule.Code == "prohibited.exam_impersonation");
        // 概述只给匹配词数量，明细才给词本身。
        Assert.All(summary.Rules, rule => Assert.True(rule.KeywordCount > 0));
        Assert.Empty(versions.Items);
    }

    [Fact]
    public void An_edit_appends_a_version_and_writes_an_admin_audit()
    {
        var world = new World();

        var updated = world.Update("把无人机列入禁止物品");

        Assert.Equal(2, updated.Version);
        Assert.False(updated.IsBuiltIn);
        Assert.Equal(world.AdminId, updated.UpdatedBy);
        Assert.Equal("把无人机列入禁止物品", updated.ChangeReason);
        Assert.Contains("新增规则 1 条", updated.ChangeSummary);

        var audit = Assert.Single(world.Audits.List(10));
        Assert.Equal(world.AdminId, audit.ActorId);
        Assert.Equal(RiskRuleCatalogService.UpdateAction, audit.Action);
        Assert.Equal(RiskRuleCatalogService.TargetType, audit.TargetType);
        Assert.StartsWith("v2：把无人机列入禁止物品", audit.Reason);
        Assert.Equal(Now, audit.OccurredAt);

        // 版本历史里能看到这一版（含摘要与依据），且只有一版。
        var version = Assert.Single(world.Rules.ListVersions(10).Items);
        Assert.Equal(2, version.Version);
        Assert.Equal("把无人机列入禁止物品", version.ChangeReason);
        Assert.Equal(world.AdminId, version.UpdatedBy);
        Assert.Equal(world.Rules.GetDetail().Rules.Count, version.RuleCount);
    }

    [Fact]
    public void A_stale_expected_version_is_a_conflict_not_a_silent_overwrite()
    {
        var world = new World();
        var stale = world.Rules.GetDetail().Version;
        world.Update("先把阈值抬到 9000 元", reward: 9000m);

        // 另一个人拿着他读到的 v1 再提交一次：必须被挡住，否则会把刚抬高的阈值覆盖回去。
        var exception = Assert.Throws<ConcurrencyConflictException>(() =>
            world.Rules.Update(Request(world.Rules.GetDetail() with { Version = stale }, "拿着旧版本再改一次", reward: 5000m), world.AdminId));

        Assert.Contains("已被其他人修改", exception.Message);
        Assert.Equal(9000m, world.Rules.GetDetail().HighRewardThreshold);
        Assert.Single(world.Rules.ListVersions(10).Items);
    }

    [Fact]
    public void A_catalog_without_any_blocked_rule_is_rejected_and_nothing_is_appended()
    {
        var world = new World();
        var detail = world.Rules.GetDetail();

        var exception = Assert.Throws<DomainException>(() => world.Rules.Update(
            new UpdateRiskRuleCatalogRequest(
                detail.Version, "只留转人工规则", detail.HighRewardThreshold, detail.NightWindowStart, detail.NightWindowEnd,
                [.. detail.Rules
                    .Where(rule => rule.Verdict == "NeedsReview")
                    .Select(rule => new RiskRuleDetailRequest(rule.Code, rule.Category, rule.Verdict, rule.Description, rule.Keywords))]),
            world.AdminId));

        Assert.Contains("至少要保留一条禁止类规则", exception.Message);
        Assert.Empty(world.Rules.ListVersions(10).Items);
        Assert.Empty(world.Audits.List(10));
    }

    [Fact]
    public void A_blank_reason_or_an_unchanged_catalog_is_rejected()
    {
        var world = new World();

        // 依据必填：即使规则内容真的改了，没写依据也不给提交。
        var blank = Assert.Throws<DomainException>(() =>
            world.Rules.Update(Request(world.Rules.GetDetail(), "   ", extraRules: [DroneRule]), world.AdminId));
        Assert.Contains("必须写明依据", blank.Message);

        // 内容没变就不该占一个版本号。
        var unchanged = Assert.Throws<DomainException>(() => world.Rules.Update(Request(world.Rules.GetDetail(), "原样保存一遍"), world.AdminId));
        Assert.Contains("没有变化", unchanged.Message);

        Assert.Empty(world.Rules.ListVersions(10).Items);
        Assert.Empty(world.Audits.List(10));
    }

    [Fact]
    public void Only_operators_can_edit_the_rules()
    {
        var world = new World();

        Assert.Throws<UnauthorizedAccessException>(() => world.Rules.Update(Request(world.Rules.GetDetail(), "匿名改规则"), Guid.Empty));
        Assert.Empty(world.Rules.ListVersions(10).Items);
    }

    [Fact]
    public void An_invalid_verdict_window_or_word_is_rejected_with_a_readable_message()
    {
        var world = new World();
        var detail = world.Rules.GetDetail();

        var badVerdict = Assert.Throws<DomainException>(() => world.Rules.Update(
            Request(detail, "结论写错", extraRules: [new RiskRuleDetailRequest("prohibited.drone", "无人机", "Allowed", "说明。", ["无人机"])]), world.AdminId));
        Assert.Contains("只能是 Blocked", badVerdict.Message);

        var badWindow = Assert.Throws<DomainException>(() => world.Rules.Update(Request(detail, "时段写错", nightEnd: "25:00"), world.AdminId));
        Assert.Contains("HH:mm", badWindow.Message);

        var oneChar = Assert.Throws<DomainException>(() => world.Rules.Update(
            Request(detail, "词太短", extraRules: [new RiskRuleDetailRequest("prohibited.drone", "无人机", "Blocked", "说明。", ["机"])]), world.AdminId));
        Assert.Contains("至少 2 个字", oneChar.Message);

        Assert.Empty(world.Rules.ListVersions(10).Items);
    }

    [Fact]
    public void The_new_version_takes_effect_for_the_next_task_created()
    {
        var world = new World();
        var earlyDraft = world.Create("明天下午帮我去前台取一份文件");

        world.Update("把无人机列入禁止物品");

        var blocked = world.Create("帮我把一台无人机送到郊区");
        Assert.Equal("Blocked", blocked.RiskVerdict);
        Assert.Equal("prohibited.no_drones", blocked.RiskRuleCode);
        Assert.Equal(2, blocked.RiskRuleVersion);

        // 早先那一条草稿的判定留在 v1：规则升级不改写历史结论。
        Assert.Equal(RiskRuleCatalog.BuiltInVersion, world.Tasks.Get(earlyDraft.Id)!.RiskRuleVersion);
    }

    [Fact]
    public void Publishing_uses_the_rules_effective_at_publish_time()
    {
        var world = new World();
        var draft = world.Create("帮我去实验楼取一份材料");
        Assert.Equal("Allowed", draft.RiskVerdict);

        // 草稿建好之后运营才收紧规则：发布这一关用的是"发布那一刻"的目录，所以同样拦得住。
        world.Update("把实验楼列入禁止进入的场所", extraRules:
            [new RiskRuleDetailRequest("prohibited.lab_break_in", "擅自进入受控场所", "Blocked", "任务涉及未经许可进入实验室等受控场所。", ["实验楼", "实验室"])]);

        var exception = Assert.Throws<DomainException>(() => world.Tasks.Publish(draft.Id, world.Owner));
        Assert.Contains("平台禁止的类别", exception.Message);

        var after = world.Tasks.Get(draft.Id)!;
        Assert.Equal("Blocked", after.RiskVerdict);
        Assert.Equal(2, after.RiskRuleVersion);
        Assert.Equal("ReadyToPublish", after.Status);
    }

    [Fact]
    public void The_version_history_lists_changes_newest_first()
    {
        var world = new World();
        world.Update("第一次：新增无人机规则", extraRules: [DroneRule]);
        // 第二次只动阈值：规则清单原样抄回来（传空数组表示不加规则），否则会因为代码重复被拒绝。
        world.Update("第二次：把阈值抬到 9000 元", reward: 9000m, extraRules: []);

        var versions = world.Rules.ListVersions(10).Items;

        Assert.Equal([3, 2], versions.Select(item => item.Version));
        Assert.Equal("第二次：把阈值抬到 9000 元", versions[0].ChangeReason);
        Assert.Contains("高金额阈值 5000 → 9000 元", versions[0].ChangeSummary);
        // 第二次提交时规则清单是从"当前生效目录"抄的，所以历史里显示的是改动后的条数。
        Assert.Equal(world.Rules.GetDetail().Rules.Count, versions[0].RuleCount);
        Assert.Equal(3, world.Rules.GetDetail().Version);
    }

    [Fact]
    public void Resetting_restores_the_built_in_catalog_as_a_new_version()
    {
        var world = new World();
        world.Update("先把无人机列为禁止类别");

        var restored = world.Rules.ResetToBuiltIn(new ResetRiskRuleCatalogRequest(2, "规则改坏了，恢复内置目录"), world.AdminId);

        Assert.Equal(3, restored.Version);
        Assert.DoesNotContain(restored.Rules, rule => rule.Code == "prohibited.no_drones");
        Assert.Equal(RiskRuleCatalog.BuiltIn.HighRewardThreshold, restored.HighRewardThreshold);
        Assert.Contains("恢复到代码内置目录", restored.ChangeSummary);

        var audit = Assert.Single(world.Audits.List(10), item => item.Action == RiskRuleCatalogService.ResetAction);
        Assert.Equal(world.AdminId, audit.ActorId);
        Assert.StartsWith("v3：规则改坏了", audit.Reason);

        // 内容已经与内置目录一致了，再点一次没有意义，也不该白占一个版本号。
        var again = Assert.Throws<DomainException>(() =>
            world.Rules.ResetToBuiltIn(new ResetRiskRuleCatalogRequest(3, "再恢复一次"), world.AdminId));
        Assert.Contains("不需要恢复", again.Message);
        Assert.Equal(2, world.Rules.ListVersions(10).Items.Count);

        // 恢复之后判定回到内置规则：v2 拦下的无人机任务在 v3 下又放行了（版本号记的是 3）。
        var task = world.Create("帮我把一台无人机送到郊区");
        Assert.Equal("Allowed", task.RiskVerdict);
        Assert.Equal(3, task.RiskRuleVersion);
    }

    private static readonly RiskRuleDetailRequest DroneRule =
        new("prohibited.no_drones", "未经许可的无人机作业", "Blocked", "任务涉及未经许可的无人机飞行，属于受管制活动。", ["无人机", "穿越机"]);

    /// <summary>按“当前生效目录 + 局部调整”拼一次提交，省掉每个用例重复抄整份清单。</summary>
    private static UpdateRiskRuleCatalogRequest Request(
        RiskRuleCatalogDetailResponse current,
        string reason,
        decimal reward = 5000m,
        string? nightStart = null,
        string? nightEnd = null,
        params RiskRuleDetailRequest[] extraRules) =>
        new(
            current.Version,
            reason,
            reward,
            nightStart ?? current.NightWindowStart,
            nightEnd ?? current.NightWindowEnd,
            [
                .. current.Rules.Select(rule => new RiskRuleDetailRequest(rule.Code, rule.Category, rule.Verdict, rule.Description, rule.Keywords)),
                .. extraRules
            ]);

    private sealed class World
    {
        public World()
        {
            Clock = new MutableTimeProvider(Now);
            AdminId = Guid.NewGuid();
            Owner = Guid.NewGuid();
            Store = new InMemoryRiskRuleCatalogStore();
            Audits = new InMemoryAdminAuditRepository();
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Rules = new RiskRuleCatalogService(Store, Audits, new EmptyUserDirectory(), Clock, new InMemoryUnitOfWork());
            Tasks = new TaskService(
                new InMemoryTaskRepository(), new InMemoryOrderRepository(), new InMemoryReviewRepository(),
                new InMemoryTaskRevisionRepository(), new EmptyUserDirectory(), Clock, Notifications,
                new InMemoryUnitOfWork(), new RiskRuleCatalogStoreProvider(Store));
        }

        public Guid AdminId { get; }
        public Guid Owner { get; }
        public MutableTimeProvider Clock { get; }
        public InMemoryRiskRuleCatalogStore Store { get; }
        public InMemoryAdminAuditRepository Audits { get; }
        public NotificationService Notifications { get; }
        public RiskRuleCatalogService Rules { get; }
        public TaskService Tasks { get; }

        /// <summary>默认改法：加一条禁止类规则（<see cref="DroneRule"/> 兜底），用来验证"改了立刻生效"。</summary>
        public RiskRuleCatalogDetailResponse Update(string reason, decimal reward = 5000m, RiskRuleDetailRequest[]? extraRules = null) =>
            Rules.Update(Request(Rules.GetDetail(), reason, reward, extraRules: extraRules ?? [DroneRule]), AdminId);

        public TaskResponse Create(string title, decimal reward = 50) => Tasks.Create(
            new CreateTaskRequest(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), reward, ["按时送达"]));
    }
}
