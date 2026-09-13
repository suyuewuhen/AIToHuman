using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确指向领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 误拦申诉的领域规则。最要紧的一条是**两档能力不同**：
/// 转人工被驳回的可以申诉成立并放行；被禁止类别命中的即使申诉成立也拿不到发布许可。
/// </summary>
public sealed class TaskRiskAppealTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    private static TaskItem Create(string title) =>
        new(Owner, title, "到前台取件", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now);

    private static TaskItem RejectedReviewTask()
    {
        var task = Create("帮我把营业执照原件送到银行");
        task.RejectRiskReview(Admin, "无法核实执照用途", Now);
        return task;
    }

    [Fact]
    public void A_rejected_review_task_can_be_appealed_and_an_accepted_appeal_releases_it()
    {
        var task = RejectedReviewTask();
        Assert.True(task.CanAppealRisk);

        task.OpenRiskAppeal(Owner, "送的是我自己公司的执照，可以补充授权说明", Now);

        Assert.Equal(RiskAppealStatus.Pending, task.RiskAppealStatus);
        Assert.Equal("送的是我自己公司的执照，可以补充授权说明", task.RiskAppealReason);
        Assert.Equal(Now, task.RiskAppealedAt);
        Assert.True(task.AwaitingRiskAppeal);
        // 待处置期间仍然不能发布。
        Assert.Throws<DomainException>(() => task.Publish(Now));

        task.ResolveRiskAppeal(Admin, accepted: true, "已核对授权说明，属于误判", Now);

        Assert.Equal(RiskAppealStatus.Accepted, task.RiskAppealStatus);
        Assert.Equal(RiskReviewStatus.Approved, task.RiskReviewStatus);
        Assert.Equal(Admin, task.RiskAppealDecidedBy);
        Assert.Equal("已核对授权说明，属于误判", task.RiskAppealDecisionNote);
        task.Publish(Now);
        Assert.Equal(TaskStatus.Published, task.Status);
    }

    [Fact]
    public void A_denied_appeal_keeps_the_task_unpublishable()
    {
        var task = RejectedReviewTask();
        task.OpenRiskAppeal(Owner, "我觉得没问题", Now);

        task.ResolveRiskAppeal(Admin, accepted: false, "材料用途仍无法核实", Now);

        Assert.Equal(RiskAppealStatus.Denied, task.RiskAppealStatus);
        Assert.Equal(RiskReviewStatus.Rejected, task.RiskReviewStatus);
        var error = Assert.Throws<DomainException>(() => task.Publish(Now));
        Assert.Contains("未通过人工复核", error.Message);
    }

    [Fact]
    public void A_blocked_task_can_be_appealed_but_an_accepted_appeal_does_not_release_it()
    {
        var task = Create("帮我代考英语四级");
        Assert.Equal(RiskVerdict.Blocked, task.RiskVerdict);
        Assert.True(task.CanAppealRisk);

        task.OpenRiskAppeal(Owner, "标题是引用别人的例子，实际是代取快递", Now);
        task.ResolveRiskAppeal(Admin, accepted: true, "确认属于规则误伤，已记录用于改进词表", Now);

        // 结论记下来了，但禁止类别依旧不能发布——人工无权放行红线。
        Assert.Equal(RiskAppealStatus.Accepted, task.RiskAppealStatus);
        Assert.Equal(RiskVerdict.Blocked, task.RiskVerdict);
        Assert.True(task.IsPublishBlockedByRisk);
        var error = Assert.Throws<DomainException>(() => task.Publish(Now));
        Assert.Contains("平台禁止的类别", error.Message);
    }

    [Fact]
    public void Editing_the_draft_invalidates_the_appeal()
    {
        var task = RejectedReviewTask();
        task.OpenRiskAppeal(Owner, "误判了", Now);

        // 正文一变，原来的申诉就不再针对同一份材料。
        task.UpdateDraft("帮我把一份普通材料送到银行", "送到柜台即可", "浦东新区", Now.AddHours(6), new Money(50), ["送到柜台"], null, null, Now);

        Assert.Equal(RiskAppealStatus.None, task.RiskAppealStatus);
        Assert.Null(task.RiskAppealReason);
        Assert.Null(task.RiskAppealDecidedBy);
        Assert.Null(task.RiskAppealDecisionNote);
        // 改完不再命中规则，因此直接可以发布。
        task.Publish(Now);
        Assert.Equal(TaskStatus.Published, task.Status);
    }

    [Fact]
    public void Only_the_owner_can_appeal()
    {
        var task = RejectedReviewTask();

        Assert.Throws<UnauthorizedAccessException>(() => task.OpenRiskAppeal(Guid.NewGuid(), "我来申诉", Now));
    }

    [Fact]
    public void An_allowed_task_has_nothing_to_appeal()
    {
        var task = Create("明天下午帮我去前台取一份文件");

        Assert.False(task.CanAppealRisk);
        Assert.Throws<DomainException>(() => task.OpenRiskAppeal(Owner, "我觉得有问题", Now));
    }

    [Fact]
    public void A_task_still_awaiting_review_cannot_be_appealed_yet()
    {
        var task = Create("帮我把营业执照原件送到银行");
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);

        var error = Assert.Throws<DomainException>(() => task.OpenRiskAppeal(Owner, "先申诉一下", Now));

        Assert.Contains("还在等人工复核", error.Message);
    }

    [Fact]
    public void An_appeal_needs_a_reason_and_cannot_be_opened_twice()
    {
        var task = RejectedReviewTask();

        Assert.Throws<DomainException>(() => task.OpenRiskAppeal(Owner, "   ", Now));
        Assert.Throws<DomainException>(() => task.OpenRiskAppeal(Owner, new string('理', TaskItem.MaxRiskAppealReasonLength + 1), Now));

        task.OpenRiskAppeal(Owner, "误判了", Now);
        var error = Assert.Throws<DomainException>(() => task.OpenRiskAppeal(Owner, "再来一次", Now));
        Assert.Contains("已经有一条待处置的申诉", error.Message);
    }

    [Fact]
    public void The_same_version_cannot_be_appealed_twice()
    {
        var task = RejectedReviewTask();
        task.OpenRiskAppeal(Owner, "误判了", Now);
        task.ResolveRiskAppeal(Admin, accepted: false, "维持原判", Now);

        // 处置过之后，同一版内容不能再申诉：要先改文案，避免把运营队列刷成申诉墙。
        Assert.False(task.CanAppealRisk);
        var error = Assert.Throws<DomainException>(() => task.OpenRiskAppeal(Owner, "再申诉一次", Now));
        Assert.Contains("已经申诉过", error.Message);

        // 改过之后按新内容重新走流程，申诉也重新可用。
        task.UpdateDraft("帮我把一份普通材料送到银行", "送到柜台即可", "浦东新区", Now.AddHours(6), new Money(50), ["送到柜台"], null, null, Now);
        Assert.Equal(RiskAppealStatus.None, task.RiskAppealStatus);
    }

    [Fact]
    public void Deciding_an_appeal_needs_a_pending_appeal_and_a_note()
    {
        var task = RejectedReviewTask();

        Assert.Throws<DomainException>(() => task.ResolveRiskAppeal(Admin, true, "放行", Now));

        task.OpenRiskAppeal(Owner, "误判了", Now);
        Assert.Throws<DomainException>(() => task.ResolveRiskAppeal(Admin, true, "  ", Now));
        Assert.Throws<DomainException>(() => task.ResolveRiskAppeal(Guid.Empty, true, "放行", Now));

        task.ResolveRiskAppeal(Admin, true, "确认误判", Now);
        // 处置过的申诉不能重复处置。
        Assert.Throws<DomainException>(() => task.ResolveRiskAppeal(Admin, false, "反悔", Now));
    }

    [Fact]
    public void An_appealed_task_that_gets_edited_keeps_the_history_of_the_appeal_decision_off()
    {
        var task = RejectedReviewTask();
        task.OpenRiskAppeal(Owner, "误判了", Now);
        task.ResolveRiskAppeal(Admin, accepted: true, "确认误判", Now);
        Assert.Equal(RiskAppealStatus.Accepted, task.RiskAppealStatus);

        task.UpdateDraft("帮我把营业执照原件送到新地址", "送到柜台即可", "浦东新区", Now.AddHours(6), new Money(50), ["送到柜台"], null, null, Now);

        // 编辑后按新文本重新走复核：申诉结论作废，任务回到待复核，而不是沿用放行。
        Assert.Equal(RiskAppealStatus.None, task.RiskAppealStatus);
        Assert.Equal(RiskReviewStatus.Pending, task.RiskReviewStatus);
        Assert.Throws<DomainException>(() => task.Publish(Now));
    }
}
