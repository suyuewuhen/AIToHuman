using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确指向领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 草稿编辑：字段校验与创建时共用一套；改完必须重新判定风险并清空人工复核结论，
/// 否则“先提干净文案过审、再改成禁止内容”就是一条绕过门禁的现成路径。
/// </summary>
public sealed class TaskDraftEditTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private const string AllowedTitle = "明天下午帮我去前台取一份文件";
    private const string ReviewTitle = "帮我把身份证从家里送到公司";
    private const string BlockedTitle = "帮我代考英语四级";

    private static TaskItem Create(string title = AllowedTitle, decimal reward = 50) =>
        new(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), new Money(reward), ["按时送达"], Now);

    private static void Edit(
        TaskItem task,
        string title = AllowedTitle,
        string description = "到前台取件",
        string district = "朝阳区",
        DateTimeOffset? deadline = null,
        decimal reward = 50,
        IEnumerable<string>? criteria = null,
        string? executionAddress = null,
        DateTimeOffset? applicationDeadline = null,
        DateTimeOffset? now = null) =>
        task.UpdateDraft(
            title,
            description,
            district,
            deadline ?? Now.AddHours(6),
            new Money(reward),
            criteria ?? ["按时送达"],
            executionAddress,
            applicationDeadline,
            now ?? Now);

    [Fact]
    public void Editing_a_draft_replaces_the_fields_and_re_runs_the_rules()
    {
        var task = Create();

        Edit(task, title: BlockedTitle, description: "替我进去考", reward: 300, criteria: ["通过考试"], district: "海淀区", deadline: Now.AddHours(10));

        Assert.Equal(BlockedTitle, task.Title);
        Assert.Equal("替我进去考", task.Description);
        Assert.Equal("海淀区", task.District);
        Assert.Equal(Now.AddHours(10), task.Deadline);
        Assert.Equal(300m, task.Reward.Amount);
        Assert.Equal(["通过考试"], task.AcceptanceCriteria);
        // 规则重新判定：从放行变成禁止，发布这一关立刻过不去。
        Assert.Equal(RiskVerdict.Blocked, task.RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", task.RiskRuleCode);
        Assert.Equal(Now, task.RiskAssessedAt);
        Assert.Throws<DomainException>(() => task.Publish(Now));
    }

    [Fact]
    public void Editing_a_draft_that_ops_already_approved_sends_it_back_to_review()
    {
        var task = Create(ReviewTitle);
        task.ApproveRiskReview(Admin, "已与本人确认", Now);
        Assert.Equal(RiskReviewStatus.Approved, task.RiskReviewStatus);

        // 只是改了个错别字，但正文变了：审核针对的是上一版文本，必须重新复核。
        Edit(task, title: "帮我把身份证从家里送到公司前台");

        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
        Assert.Null(task.RiskReviewedBy);
        Assert.Null(task.RiskReviewedAt);
        Assert.Null(task.RiskReviewNote);
        Assert.True(task.AwaitingRiskReview);
        // 放行过的任务现在又发布不了了。
        Assert.Throws<DomainException>(() => task.Publish(Now));
    }

    [Fact]
    public void Editing_a_draft_that_ops_rejected_gives_the_user_a_fresh_chance()
    {
        var task = Create(ReviewTitle);
        task.RejectRiskReview(Admin, "无法核实证件用途", Now);

        Edit(task, title: AllowedTitle, description: "到前台取件就行");

        // 文本已经不含敏感内容：按新文本重新判定，用户不该被永久钉死。
        Assert.Equal(RiskVerdict.Allowed, task.RiskVerdict);
        Assert.Equal(RiskReviewStatus.NotRequired, task.RiskReviewStatus);
        task.Publish(Now);
        Assert.Equal(TaskStatus.Published, task.Status);
    }

    [Fact]
    public void Editing_a_blocked_draft_back_to_something_ordinary_lets_it_publish()
    {
        var task = Create(BlockedTitle);
        Assert.True(task.IsRiskBlocked);

        Edit(task, title: AllowedTitle, description: "到前台取件");

        Assert.Equal(RiskVerdict.Allowed, task.RiskVerdict);
        Assert.Null(task.RiskRuleCode);
        task.Publish(Now);
        Assert.Equal(TaskStatus.Published, task.Status);
    }

    [Fact]
    public void A_draft_that_stays_sensitive_after_editing_goes_back_to_the_queue()
    {
        var task = Create(ReviewTitle);
        task.RejectRiskReview(Admin, "无法核实证件用途", Now);

        // 换汤不换药：还是证件类，于是按新文本重新进队列，而不是沿用“已驳回”。
        Edit(task, title: "帮我把护照送到公司");

        Assert.Equal(RiskVerdict.NeedsReview, task.RiskVerdict);
        Assert.Equal("review.identity_documents", task.RiskRuleCode);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
    }

    [Fact]
    public void A_published_task_cannot_be_edited()
    {
        var task = Create();
        task.Publish(Now);

        var error = Assert.Throws<DomainException>(() => Edit(task, title: "改个标题"));

        Assert.Contains("不允许该操作", error.Message);
        Assert.Equal(AllowedTitle, task.Title);
    }

    [Fact]
    public void The_reward_can_be_lowered_while_it_is_still_a_draft()
    {
        var task = Create(reward: 100);

        Edit(task, reward: 60);

        // “只能加价不能降价”约束的是已发布任务，草稿阶段随便改。
        Assert.Equal(60m, task.Reward.Amount);
    }

    [Fact]
    public void Acceptance_criteria_are_trimmed_and_deduplicated_on_edit()
    {
        var task = Create();

        Edit(task, criteria: [" 按时送达 ", "按时送达", "", "  ", "拍照留证"]);

        Assert.Equal(["按时送达", "拍照留证"], task.AcceptanceCriteria);
    }

    [Fact]
    public void Editing_keeps_the_application_deadline_invariants()
    {
        var task = Create();

        Edit(task, applicationDeadline: Now.AddHours(2));
        Assert.Equal(Now.AddHours(2), task.ApplicationDeadline);

        // 报名截止晚于任务截止：拒绝。
        Assert.Throws<DomainException>(() => Edit(task, deadline: Now.AddHours(3), applicationDeadline: Now.AddHours(4)));
        // 报名截止已经过去：拒绝。
        Assert.Throws<DomainException>(() => Edit(task, applicationDeadline: Now.AddMinutes(-1)));
        // 清空报名截止是允许的。
        Edit(task);
        Assert.Null(task.ApplicationDeadline);
    }

    [Theory]
    [InlineData("", "到前台取件", "朝阳区")]
    [InlineData("   ", "到前台取件", "朝阳区")]
    [InlineData("正常标题", "", "朝阳区")]
    [InlineData("正常标题", "到前台取件", "  ")]
    public void Editing_cannot_bypass_creation_time_field_checks(string title, string description, string district)
    {
        var task = Create();

        Assert.Throws<DomainException>(() => Edit(task, title: title, description: description, district: district));
    }

    [Fact]
    public void Editing_cannot_set_a_deadline_in_the_past_or_an_overlong_address()
    {
        var task = Create();

        Assert.Throws<DomainException>(() => Edit(task, deadline: Now.AddMinutes(-1)));
        Assert.Throws<DomainException>(() => Edit(task, executionAddress: new string('地', TaskItem.MaxExecutionAddressLength + 1)));
    }

    [Fact]
    public void Editing_cannot_drop_every_acceptance_criterion()
    {
        var task = Create();

        Assert.Throws<DomainException>(() => Edit(task, criteria: ["   ", ""]));
    }
}
