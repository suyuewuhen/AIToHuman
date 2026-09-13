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

    private static TaskItem Create(string title = "明天下午帮我去前台取一份文件") =>
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
        Assert.Equal(RiskRuleCatalog.Version, revision.RiskRuleVersion);
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
        Assert.Equal(RiskRuleCatalog.Version, revision.RiskRuleVersion);
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
