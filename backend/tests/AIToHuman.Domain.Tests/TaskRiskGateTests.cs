using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确指向领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>风险门禁在任务聚合上的行为：禁止类别锁死、转人工要等复核、复核结论不可反复改。</summary>
public sealed class TaskRiskGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static TaskItem Create(string title, string description = "到前台取件", decimal reward = 50, DateTimeOffset? deadline = null) =>
        new(Owner, title, description, "朝阳区", deadline ?? Now.AddHours(6), new Money(reward), ["按时送达"], Now);

    [Fact]
    public void A_prohibited_task_is_assessed_as_blocked_at_creation()
    {
        var task = Create("帮我代考英语四级");

        Assert.Equal(RiskVerdict.Blocked, task.RiskVerdict);
        Assert.Equal("prohibited.exam_impersonation", task.RiskRuleCode);
        Assert.Equal(RiskRuleCatalog.Version, task.RiskRuleVersion);
        Assert.Equal(Now, task.RiskAssessedAt);
        Assert.True(task.IsRiskBlocked);
        Assert.True(task.IsPublishBlockedByRisk);
        // 禁止类别不进人工队列：人工无权放行。
        Assert.Equal(RiskReviewStatus.NotRequired, task.RiskReviewStatus);
    }

    [Fact]
    public void A_prohibited_task_cannot_be_published()
    {
        var task = Create("帮我代考英语四级");

        var error = Assert.Throws<DomainException>(() => task.Publish(Now));

        Assert.Contains("禁止", error.Message);
        Assert.Contains("prohibited.exam_impersonation", error.Message);
        Assert.Equal(TaskStatus.ReadyToPublish, task.Status);
    }

    [Fact]
    public void The_owner_can_still_cancel_a_prohibited_draft()
    {
        var task = Create("帮我代考英语四级");

        task.Cancel("不发了", Now);

        Assert.Equal(TaskStatus.Cancelled, task.Status);
    }

    [Fact]
    public void A_review_task_waits_for_human_review_before_publishing()
    {
        var task = Create("帮我把身份证从家里送到公司");

        Assert.Equal(RiskVerdict.NeedsReview, task.RiskVerdict);
        Assert.Equal("review.identity_documents", task.RiskRuleCode);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
        Assert.True(task.AwaitingRiskReview);

        var error = Assert.Throws<DomainException>(() => task.Publish(Now));
        Assert.Contains("人工复核", error.Message);
    }

    [Fact]
    public void An_approved_task_can_be_published()
    {
        var task = Create("帮我把身份证从家里送到公司");

        task.ApproveRiskReview(Admin, "已与本人确认是自用证件", Now);
        task.Publish(Now);

        Assert.Equal(TaskStatus.Published, task.Status);
        Assert.Equal(RiskReviewStatus.Approved, task.RiskReviewStatus);
        Assert.Equal(Admin, task.RiskReviewedBy);
        Assert.Equal(Now, task.RiskReviewedAt);
        Assert.False(task.IsPublishBlockedByRisk);
    }

    [Fact]
    public void A_rejected_task_cannot_be_published_and_keeps_the_reason()
    {
        var task = Create("帮我把身份证从家里送到公司");

        task.RejectRiskReview(Admin, "无法核实证件用途", Now);

        var error = Assert.Throws<DomainException>(() => task.Publish(Now));
        Assert.Contains("未通过人工复核", error.Message);
        Assert.Contains("无法核实证件用途", error.Message);
    }

    [Fact]
    public void A_rejected_task_stays_rejected_even_if_the_rules_no_longer_flag_it()
    {
        var task = Create("帮我把身份证从家里送到公司");
        task.RejectRiskReview(Admin, "无法核实证件用途", Now);

        // 用 Rehydrate 模拟“规则或字段后来变了”的重新判定路径：人工驳回是终态，不会被规则洗白。
        var restored = TaskItem.Rehydrate(
            task.Id, Owner, "帮我把一份普通材料送到公司", "送到前台即可", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now,
            TaskStatus.ReadyToPublish, [],
            riskVerdict: RiskVerdict.Allowed, riskRuleVersion: RiskRuleCatalog.Version, riskAssessedAt: Now,
            riskReviewStatus: RiskReviewStatus.Rejected, riskReviewedBy: Admin, riskReviewedAt: Now, riskReviewNote: "无法核实证件用途");

        var error = Assert.Throws<DomainException>(() => restored.Publish(Now));

        Assert.Equal(RiskReviewStatus.Rejected, restored.RiskReviewStatus);
        Assert.Contains("未通过人工复核", error.Message);
    }

    [Fact]
    public void A_review_decision_needs_a_reason()
    {
        var task = Create("帮我把身份证从家里送到公司");

        Assert.Throws<DomainException>(() => task.ApproveRiskReview(Admin, "   ", Now));

        var error = Assert.Throws<DomainException>(() => task.RejectRiskReview(Admin, new string('长', TaskItem.MaxRiskReviewNoteLength + 1), Now));
        Assert.Contains("长度不超过", error.Message);
    }

    [Fact]
    public void A_review_decision_needs_a_reviewer()
    {
        var task = Create("帮我把身份证从家里送到公司");

        Assert.Throws<DomainException>(() => task.ApproveRiskReview(Guid.Empty, "放行", Now));
    }

    [Fact]
    public void A_review_cannot_be_decided_twice()
    {
        var task = Create("帮我把身份证从家里送到公司");
        task.ApproveRiskReview(Admin, "放行", Now);

        var error = Assert.Throws<DomainException>(() => task.RejectRiskReview(Admin, "反悔", Now));

        Assert.Contains("已经处置过", error.Message);
    }

    [Fact]
    public void An_allowed_task_has_nothing_to_review()
    {
        var task = Create("明天下午帮我去前台取一份文件");

        Assert.Equal(RiskVerdict.Allowed, task.RiskVerdict);
        Assert.Equal(RiskReviewStatus.NotRequired, task.RiskReviewStatus);
        Assert.Null(task.RiskRuleCode);
        Assert.False(task.IsPublishBlockedByRisk);

        Assert.Throws<DomainException>(() => task.ApproveRiskReview(Admin, "放行", Now));
    }

    [Fact]
    public void A_high_reward_draft_goes_to_review_before_it_can_be_published()
    {
        var task = Create("帮我送一份文件到郊区", reward: RiskRuleCatalog.HighRewardThreshold + 500);

        Assert.Equal("review.high_reward", task.RiskRuleCode);
        Assert.True(task.AwaitingRiskReview);
        task.ApproveRiskReview(Admin, "已核实金额与内容匹配", Now);
        task.Publish(Now);

        Assert.Equal(TaskStatus.Published, task.Status);
    }

    [Fact]
    public void An_allowed_task_publishes_without_any_review()
    {
        var task = Create("明天下午帮我去前台取一份文件");

        task.Publish(Now);

        Assert.Equal(TaskStatus.Published, task.Status);
        Assert.Equal(RiskReviewStatus.NotRequired, task.RiskReviewStatus);
    }
}
