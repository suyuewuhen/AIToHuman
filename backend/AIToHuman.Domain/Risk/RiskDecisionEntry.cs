using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Risk;

/// <summary>这次判定是**因为什么动作**跑的：事后看统计时，"哪一类改动最容易触发规则"是很关键的信号。</summary>
public enum RiskDecisionReason
{
    /// <summary>创建草稿。</summary>
    Created,

    /// <summary>编辑草稿。</summary>
    Edited,

    /// <summary>回滚到历史某一版。</summary>
    Restored,

    /// <summary>发布前的最后一道判定。</summary>
    Published,

    /// <summary>已发布任务加价（悬赏是规则输入）。</summary>
    RewardRaised,

    /// <summary>规则升级后的发布后复检。</summary>
    Rechecked
}

/// <summary>
/// 每一次风险判定的留档：**只追加**，一行就是"某条任务在某次动作后被判成什么"。
///
/// 为什么需要它：任务行上只保留**最新一次**判定，于是"这条规则到底拦了多少次、误伤多少、
/// 加词之后有没有好转"这些问题在库里根本无从回答——规则调参只能凭感觉。
/// 判定次数与原因代码是规则质量的唯一客观依据，因此单独留档，
/// 并且与 `task_risk_appeals`（申诉留档）一起构成"命中 → 误伤"的闭环统计。
/// </summary>
public sealed class RiskDecisionEntry
{
    private RiskDecisionEntry(
        Guid id,
        Guid taskId,
        RiskDecisionReason reason,
        RiskVerdict verdict,
        string? ruleCode,
        string? category,
        int ruleVersion,
        decimal rewardAmount,
        DateTimeOffset occurredAt)
    {
        Id = id;
        TaskId = taskId;
        Reason = reason;
        Verdict = verdict;
        RuleCode = ruleCode;
        Category = category;
        RuleVersion = ruleVersion;
        RewardAmount = rewardAmount;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    public Guid TaskId { get; }

    public RiskDecisionReason Reason { get; }

    public RiskVerdict Verdict { get; }

    /// <summary>命中的原因代码；放行时为空。</summary>
    public string? RuleCode { get; }

    public string? Category { get; }

    /// <summary>判定时使用的规则目录版本。</summary>
    public int RuleVersion { get; }

    /// <summary>判定时的悬赏金额（阈值规则的依据，事后看统计时需要）。</summary>
    public decimal RewardAmount { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>按任务当前的判定结果记一条留痕（判定本身已经写回任务，这里只负责把它固定下来）。</summary>
    public static RiskDecisionEntry Record(TaskItem task, RiskDecisionReason reason, DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            task.Id,
            reason,
            task.RiskVerdict,
            task.RiskRuleCode,
            task.RiskCategory,
            task.RiskRuleVersion,
            task.Reward.Amount,
            UtcTimestamp.Normalize(now));

    public static RiskDecisionEntry Rehydrate(
        Guid id,
        Guid taskId,
        RiskDecisionReason reason,
        RiskVerdict verdict,
        string? ruleCode,
        string? category,
        int ruleVersion,
        decimal rewardAmount,
        DateTimeOffset occurredAt) =>
        new(id, taskId, reason, verdict, ruleCode, category, ruleVersion, rewardAmount, UtcTimestamp.Normalize(occurredAt));
}
