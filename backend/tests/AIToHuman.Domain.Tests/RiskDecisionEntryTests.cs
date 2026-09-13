using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 判定留痕的领域规则：一行就是"某条任务在某次动作后被判成什么"，判定结果与规则版本都要扣上，
/// 否则统计出来的是"大概拦了多少"，而不是"哪条规则拦了多少"。
/// </summary>
public sealed class RiskDecisionEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();

    [Fact]
    public void A_record_captures_the_verdict_the_rule_and_the_reason()
    {
        var task = Blocked();

        var entry = RiskDecisionEntry.Record(task, RiskDecisionReason.Created, Now);

        Assert.Equal(task.Id, entry.TaskId);
        Assert.Equal(RiskDecisionReason.Created, entry.Reason);
        Assert.Equal(RiskVerdict.Blocked, entry.Verdict);
        Assert.Equal("prohibited.exam_impersonation", entry.RuleCode);
        Assert.False(string.IsNullOrWhiteSpace(entry.Category));
        Assert.Equal(task.RiskRuleVersion, entry.RuleVersion);
        Assert.Equal(50m, entry.RewardAmount);
        Assert.Equal(Now, entry.OccurredAt);
    }

    [Fact]
    public void An_allowed_decision_has_no_rule_code_but_is_still_recorded()
    {
        // 放行也要留痕：没有"放行了多少次"，就说不清规则收紧之后命中率是真降了还是没人发任务了。
        var task = Allowed();

        var entry = RiskDecisionEntry.Record(task, RiskDecisionReason.Published, Now);

        Assert.Equal(RiskVerdict.Allowed, entry.Verdict);
        Assert.Null(entry.RuleCode);
        Assert.Equal(RiskDecisionReason.Published, entry.Reason);
    }

    [Fact]
    public void Every_assessment_reason_is_distinguishable()
    {
        // 六个记录点必须能被区分开：把它们混成一个"判定过"，统计里就看不出是哪一类改动触发的。
        var reasons = Enum.GetValues<RiskDecisionReason>();

        Assert.Contains(RiskDecisionReason.Created, reasons);
        Assert.Contains(RiskDecisionReason.Edited, reasons);
        Assert.Contains(RiskDecisionReason.Restored, reasons);
        Assert.Contains(RiskDecisionReason.Published, reasons);
        Assert.Contains(RiskDecisionReason.RewardRaised, reasons);
        Assert.Contains(RiskDecisionReason.Rechecked, reasons);
    }

    private static TaskItem Blocked() =>
        new(Owner, "帮我代考英语四级", "考试要过", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now);

    private static TaskItem Allowed()
    {
        var task = new TaskItem(Owner, "帮我去前台取一份文件", "到前台取件", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now);
        task.Publish(Now);
        return task;
    }
}
