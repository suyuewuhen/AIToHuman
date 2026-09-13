using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确指向领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 草稿版本快照：每次编辑都要能回答"改了什么、谁改的、当时风险结论是什么"。
/// </summary>
public sealed class TaskDraftRevisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private const string AllowedTitle = "明天下午帮我去前台取一份文件";
    private const string ReviewTitle = "帮我把身份证从家里送到公司";
    private const string BlockedTitle = "帮我代考英语四级";

    private static TaskItem Create(string title = AllowedTitle) =>
        new(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now);

    private static IReadOnlyCollection<string> Edit(
        TaskItem task,
        string title = "明天下午帮我去前台取一份文件",
        string description = "到前台取件",
        string district = "朝阳区",
        DateTimeOffset? deadline = null,
        decimal reward = 50,
        IEnumerable<string>? criteria = null,
        string? executionAddress = null) =>
        task.UpdateDraft(title, description, district, deadline ?? Now.AddHours(6), new Money(reward), criteria ?? ["按时送达"], executionAddress, null, Now);

    [Fact]
    public void The_first_revision_snapshots_the_created_draft()
    {
        var task = Create();

        var revision = TaskDraftRevision.Initial(task, Owner, Now);

        Assert.Equal(1, revision.Revision);
        Assert.Equal(task.Id, revision.TaskId);
        Assert.Equal("创建草稿", revision.ChangeSummary);
        Assert.Equal(Owner, revision.EditedBy);
        Assert.Equal(task.Title, revision.Title);
        Assert.Equal(task.Description, revision.Description);
        Assert.Equal(task.District, revision.District);
        Assert.Equal(task.Deadline, revision.Deadline);
        Assert.Equal(50m, revision.RewardAmount);
        Assert.Equal("CNY", revision.RewardCurrency);
        Assert.Equal(["按时送达"], revision.AcceptanceCriteria);
        Assert.Equal(nameof(RiskVerdict.Allowed), revision.RiskVerdict);
        Assert.Equal(RiskRuleCatalog.BuiltIn.Version, revision.RiskRuleVersion);
        Assert.Equal(Now, revision.CreatedAt);
    }

    [Fact]
    public void Editing_reports_only_the_fields_that_actually_changed()
    {
        // 每次都从新建的草稿开始：编辑是"就地改状态"，用同一个实例会带上上一次的改动。
        Assert.Equal(["标题"], Edit(Create(), title: "明天下午帮我去公司前台取一份文件"));
        Assert.Equal(["悬赏"], Edit(Create(), reward: 80));
        Assert.Equal(["验收标准"], Edit(Create(), criteria: ["送到前台并拍照"]));
        Assert.Empty(Edit(Create()));
    }

    [Fact]
    public void Renaming_several_fields_at_once_lists_them_all()
    {
        var changed = Edit(Create(), title: "改成去菜市场买两斤苹果", district: "西城区", reward: 66, criteria: ["苹果完好"]);

        Assert.Equal(["标题", "公开区域", "悬赏", "验收标准"], changed);
    }

    [Fact]
    public void A_revision_records_the_risk_verdict_of_that_version()
    {
        var task = Create();
        Edit(task, title: "帮我代考英语四级", description: "替我进考场");

        var revision = TaskDraftRevision.FromEdit(task, 2, Owner, ["标题", "描述"], Now);

        // 历史要能对上"第 2 版被判成禁止、用的是第 1 版规则"。
        Assert.Equal(nameof(RiskVerdict.Blocked), revision.RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", revision.RiskRuleCode);
        Assert.Equal(RiskRuleCatalog.BuiltIn.Version, revision.RiskRuleVersion);
        Assert.Equal("标题、描述", revision.ChangeSummary);
    }

    [Fact]
    public void A_long_change_list_collapses_into_a_readable_summary()
    {
        var task = Create();

        var revision = TaskDraftRevision.FromEdit(task, 2, Owner, ["标题", "描述", "公开区域", "截止时间", "悬赏", "验收标准", "执行地址"], Now);

        Assert.Equal("标题、描述、公开区域、截止时间、悬赏、验收标准 等 7 项", revision.ChangeSummary);
    }

    [Fact]
    public void An_empty_change_list_is_still_a_valid_revision()
    {
        var revision = TaskDraftRevision.FromEdit(Create(), 2, Owner, [], Now);

        Assert.Equal("无字段变化", revision.ChangeSummary);
    }

    [Fact]
    public void A_revision_must_record_the_task_and_the_editor()
    {
        var task = Create();

        Assert.Throws<DomainException>(() => TaskDraftRevision.FromEdit(task, 2, Guid.Empty, ["标题"], Now));
        Assert.Throws<DomainException>(() => TaskDraftRevision.FromEdit(task, 0, Owner, ["标题"], Now));
    }

    [Fact]
    public void The_diff_lists_each_changed_field_with_before_and_after()
    {
        var task = Create();
        var first = TaskDraftRevision.Initial(task, Owner, Now);

        Edit(task, title: "改成去菜市场买两斤苹果", district: "西城区", reward: 66, criteria: ["苹果完好", "送到家门口"]);
        var second = TaskDraftRevision.FromEdit(task, 2, Owner, ["标题", "公开区域", "悬赏", "验收标准"], Now);

        Edit(task, title: "改成去菜市场买两斤苹果", district: "西城区", deadline: Now.AddHours(9), reward: 66, criteria: ["苹果完好", "送到家门口"]);
        var third = TaskDraftRevision.FromEdit(task, 3, Owner, ["截止时间"], Now);

        // 第一版没有可比对象，差异为空。
        Assert.Empty(TaskDraftRevision.Diff(null, first));

        var firstToSecond = TaskDraftRevision.Diff(first, second);
        Assert.Equal(["标题", "公开区域", "悬赏", "验收标准"], firstToSecond.Select(item => item.Field));
        Assert.Equal("明天下午帮我去前台取一份文件", firstToSecond[0].Before);
        Assert.Equal("改成去菜市场买两斤苹果", firstToSecond[0].After);
        Assert.Equal("50 CNY", firstToSecond[2].Before);
        Assert.Equal("66 CNY", firstToSecond[2].After);
        Assert.Equal("按时送达", firstToSecond[3].Before);
        Assert.Equal("苹果完好；送到家门口", firstToSecond[3].After);

        var secondToThird = TaskDraftRevision.Diff(second, third);
        var deadline = Assert.Single(secondToThird);
        Assert.Equal("截止时间", deadline.Field);
        // 时间统一格式化成 UTC 文本，前端不需要自己再实现一套格式化规则。
        Assert.Equal(Now.AddHours(6).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture), deadline.Before);
        Assert.Equal(Now.AddHours(9).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture), deadline.After);
    }

    [Fact]
    public void The_diff_is_empty_when_the_snapshot_is_identical()
    {
        var task = Create();
        var first = TaskDraftRevision.Initial(task, Owner, Now);
        var same = TaskDraftRevision.FromEdit(task, 2, Owner, [], Now);

        Assert.Empty(TaskDraftRevision.Diff(first, same));
    }

    [Fact]
    public void A_restored_revision_says_where_it_came_from()
    {
        var task = Create();
        var restore = TaskDraftRevision.FromRestore(task, 3, sourceRevision: 1, Owner, ["标题", "悬赏"], Now);

        Assert.Equal("回滚自第 1 版：标题、悬赏", restore.ChangeSummary);
    }

    [Fact]
    public void Restoring_an_identical_revision_is_still_marked_as_a_restore()
    {
        var task = Create();
        var restore = TaskDraftRevision.FromRestore(task, 3, sourceRevision: 2, Owner, [], Now);

        // 即使内容一样也要看得出"这一版是回滚产生的"，否则历史里会出现来路不明的一版。
        Assert.Equal("回滚自第 2 版（内容与该版一致）", restore.ChangeSummary);
    }

    [Fact]
    public void Restoring_applies_the_snapshot_through_the_same_validation_as_editing()
    {
        var task = Create();
        var allowedSnapshot = TaskDraftRevision.Initial(task, Owner, Now);

        // 同一个任务先变成禁止内容（第 2 版），再改回普通内容（第 3 版）。
        Edit(task, title: BlockedTitle, description: "替我进考场");
        var blockedSnapshot = TaskDraftRevision.FromEdit(task, 2, Owner, ["标题", "描述"], Now);
        Edit(task, title: AllowedTitle, description: "到前台取件");
        Assert.Equal(RiskVerdict.Allowed, task.RiskVerdict);

        // 回滚到第 2 版：字段回来了，风险也跟着重新判定成禁止。
        Assert.Equal(["标题", "描述"], task.RestoreDraft(blockedSnapshot, Now));
        Assert.Equal(BlockedTitle, task.Title);
        Assert.Equal(RiskVerdict.Blocked, task.RiskVerdict);
        Assert.Throws<DomainException>(() => task.Publish(Now));

        // 再回滚到第 1 版：又能发布了——回滚不是单向的。
        Assert.Equal(["标题", "描述"], task.RestoreDraft(allowedSnapshot, Now));
        Assert.Equal(RiskVerdict.Allowed, task.RiskVerdict);
        task.Publish(Now);
    }

    [Fact]
    public void Restoring_an_expired_deadline_is_refused_instead_of_silently_producing_a_dead_draft()
    {
        var task = Create();
        var snapshot = TaskDraftRevision.Initial(task, Owner, Now);
        var tooLate = Now.AddDays(3);

        // 三天后再回滚这一版：它的截止时间已经过去，回滚应当像编辑一样被拒绝。
        var error = Assert.Throws<DomainException>(() => task.RestoreDraft(snapshot, tooLate));
        Assert.Contains("截止时间必须晚于当前时间", error.Message);
    }

    [Fact]
    public void Restoring_a_revision_from_another_task_is_refused()
    {
        var task = Create();
        var foreign = TaskDraftRevision.Initial(Create("别的任务"), Owner, Now);

        var error = Assert.Throws<DomainException>(() => task.RestoreDraft(foreign, Now));
        Assert.Contains("不属于当前任务", error.Message);
    }

    [Fact]
    public void Restoring_clears_the_review_and_appeal_state_like_an_edit_does()
    {
        var task = Create(ReviewTitle);
        // 快照必须来自同一个任务：回滚不允许拿别的任务的版本。
        var snapshot = TaskDraftRevision.Initial(task, Owner, Now);
        task.RejectRiskReview(Admin, "无法核实证件用途", Now);
        task.OpenRiskAppeal(Owner, "误判了", Now);

        task.RestoreDraft(snapshot, Now);

        // 回滚会重新判定风险并作废人工复核与申诉：这条任务回到"待复核"，而不是沿用被驳回或申诉中的状态。
        Assert.Equal(RiskAppealStatus.None, task.RiskAppealStatus);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
        Assert.Equal(RiskVerdict.NeedsReview, task.RiskVerdict);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(13)]
    public void Draft_snapshots_need_field_limits_so_that_history_is_storable(int marker)
    {
        var task = Create();

        if (marker == 401)
        {
            // 描述超长：创建与编辑走同一套校验，所以两条路径都要拒绝。
            var longText = new string('说', TaskDraftRevision.MaxDescriptionLength + 1);
            Assert.Throws<DomainException>(() => Edit(task, description: longText));
        }
        else
        {
            var tooMany = Enumerable.Range(1, TaskDraftRevision.MaxCriteriaCount + 1).Select(index => $"第 {index} 项").ToArray();
            Assert.Throws<DomainException>(() => Edit(task, criteria: tooMany));
        }
    }

    [Fact]
    public void A_single_acceptance_criterion_cannot_be_longer_than_the_cap()
    {
        var task = Create();

        Assert.Throws<DomainException>(() => Edit(task, criteria: [new string('标', TaskDraftRevision.MaxCriterionLength + 1)]));
    }

    [Fact]
    public void A_district_longer_than_the_cap_is_refused()
    {
        var task = Create();

        Assert.Throws<DomainException>(() => Edit(task, district: new string('区', TaskDraftRevision.MaxDistrictLength + 1)));
    }
}
