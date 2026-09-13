using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 申诉留档与节流口径的领域约束：一次申诉一行、结论只写一次、理由必填，
/// 以及"提交时用的哪一版规则"必须跟着记录一起留下来。
/// </summary>
public sealed class RiskAppealRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    [Fact]
    public void Submitting_a_record_captures_the_rule_that_blocked_it()
    {
        var task = BlockedTask();

        var record = RiskAppealRecord.Submit(task, Owner, "  这只是在郊区拍风景，不是商用作业  ", Now);

        Assert.Equal(task.Id, record.TaskId);
        Assert.Equal(Owner, record.OwnerId);
        // 提交时的规则代码与版本要一起留档：事后复盘"当时是哪一版规则拦的"靠它。
        Assert.Equal(task.RiskRuleCode, record.RuleCode);
        Assert.Equal(task.RiskRuleVersion, record.RuleVersion);
        Assert.Equal(RiskVerdict.Blocked, record.Verdict);
        Assert.Equal("这只是在郊区拍风景，不是商用作业", record.Reason);
        Assert.Equal(RiskAppealStatus.Pending, record.Status);
        Assert.Equal(Now, record.SubmittedAt);
        Assert.Null(record.DecidedBy);
    }

    [Fact]
    public void A_record_cannot_be_submitted_by_someone_else()
    {
        var task = BlockedTask();

        Assert.Throws<UnauthorizedAccessException>(() => RiskAppealRecord.Submit(task, Guid.NewGuid(), "我不是所有者", Now));
    }

    [Fact]
    public void A_record_needs_a_reason_within_the_limit()
    {
        var task = BlockedTask();

        Assert.Throws<DomainException>(() => RiskAppealRecord.Submit(task, Owner, "   ", Now));
        Assert.Throws<DomainException>(() => RiskAppealRecord.Submit(task, Owner, new string('理', TaskItem.MaxRiskAppealReasonLength + 1), Now));
    }

    [Fact]
    public void A_record_can_only_be_decided_once()
    {
        var record = RiskAppealRecord.Submit(BlockedTask(), Owner, "误判", Now);

        record.Decide(Admin, accepted: true, "已核实是正常航拍", Now.AddHours(1));

        Assert.Equal(RiskAppealStatus.Accepted, record.Status);
        Assert.Equal(Admin, record.DecidedBy);
        Assert.Equal(Now.AddHours(1), record.DecidedAt);
        Assert.Equal("已核实是正常航拍", record.DecisionNote);

        Assert.Throws<DomainException>(() => record.Decide(Admin, accepted: false, "再判一次", Now.AddHours(2)));
    }

    [Fact]
    public void Deciding_requires_a_reviewer_and_a_note()
    {
        var record = RiskAppealRecord.Submit(BlockedTask(), Owner, "误判", Now);

        Assert.Throws<DomainException>(() => record.Decide(Guid.Empty, accepted: true, "已核实", Now));
        Assert.Throws<DomainException>(() => record.Decide(Admin, accepted: true, "  ", Now));
    }

    [Fact]
    public void The_throttle_caps_are_locked_by_tests()
    {
        // 这两条上限是防刷运营队列的底线，改动必须是有意识的（用例会红）。
        Assert.Equal(3, RiskAppealPolicy.MaxPerTask);
        Assert.Equal(5, RiskAppealPolicy.MaxPerOwnerPerDay);
        Assert.Equal(TimeSpan.FromDays(1), RiskAppealPolicy.Window);
    }

    private static TaskItem BlockedTask() =>
        new(Owner, "帮我代考英语四级", "考试要过", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now);
}
