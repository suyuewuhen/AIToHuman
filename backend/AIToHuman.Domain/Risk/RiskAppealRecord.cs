using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Risk;

/// <summary>
/// 一次误拦申诉的留档：一次申诉一行，只追加；运营给出结论后把结论写回同一行（不新增行）。
///
/// 为什么单独留档而不是只把最新结论记在任务上：任务上只能存一份"当前申诉状态"，
/// 一旦所有者改了文案，上一轮的申诉理由与运营结论就被覆盖掉了——
/// 而"这条任务被误拦过几次、每次运营怎么说的"恰恰是复盘规则质量的关键材料。
/// 记录里同时保存提交时的规则代码与版本，于是"当时是哪一版规则拦的"也能对上。
/// </summary>
public sealed class RiskAppealRecord
{
    /// <summary>运营结论依据的长度上限（与复核依据同一口径）。</summary>
    public const int MaxDecisionNoteLength = 200;

    private RiskAppealRecord(
        Guid id,
        Guid taskId,
        Guid ownerId,
        string? ruleCode,
        int ruleVersion,
        RiskVerdict verdict,
        string reason,
        DateTimeOffset submittedAt,
        RiskAppealStatus status,
        Guid? decidedBy,
        DateTimeOffset? decidedAt,
        string? decisionNote)
    {
        Id = id;
        TaskId = taskId;
        OwnerId = ownerId;
        RuleCode = ruleCode;
        RuleVersion = ruleVersion;
        Verdict = verdict;
        Reason = reason;
        SubmittedAt = submittedAt;
        Status = status;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
        DecisionNote = decisionNote;
    }

    public Guid Id { get; }

    public Guid TaskId { get; }

    public Guid OwnerId { get; }

    /// <summary>提交时命中的原因代码（放行后重新申诉的话，这里记录的是那一次的命中）。</summary>
    public string? RuleCode { get; }

    /// <summary>提交时的规则目录版本。</summary>
    public int RuleVersion { get; }

    /// <summary>提交时的风险结论（Blocked 或 NeedsReview）。</summary>
    public RiskVerdict Verdict { get; }

    /// <summary>所有者填写的申诉理由。</summary>
    public string Reason { get; }

    public DateTimeOffset SubmittedAt { get; }

    /// <summary>Pending / Accepted（误伤成立）/ Denied（维持原判）。</summary>
    public RiskAppealStatus Status { get; private set; }

    public Guid? DecidedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionNote { get; private set; }

    /// <summary>记一次申诉提交（理由必填，长度与领域规则一致）。</summary>
    public static RiskAppealRecord Submit(TaskItem task, Guid ownerId, string reason, DateTimeOffset now)
    {
        if (ownerId != task.OwnerId) throw new UnauthorizedAccessException("只有任务所有者可以申诉。");

        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > TaskItem.MaxRiskAppealReasonLength)
        {
            throw new DomainException($"申诉必须说明理由，长度不超过 {TaskItem.MaxRiskAppealReasonLength} 个字符。");
        }

        return new RiskAppealRecord(
            Guid.NewGuid(), task.Id, ownerId, task.RiskRuleCode, task.RiskRuleVersion, task.RiskVerdict,
            trimmed, UtcTimestamp.Normalize(now), RiskAppealStatus.Pending, null, null, null);
    }

    /// <summary>运营给出结论：只能处置一次，依据必填。</summary>
    public void Decide(Guid reviewerId, bool accepted, string note, DateTimeOffset now)
    {
        if (Status != RiskAppealStatus.Pending) throw new DomainException("这次申诉已经处置过，不能重复处置。");
        if (reviewerId == Guid.Empty) throw new DomainException("申诉处置必须记录处置人。");

        var trimmed = note?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > MaxDecisionNoteLength)
        {
            throw new DomainException($"申诉处置必须填写依据，长度不超过 {MaxDecisionNoteLength} 个字符。");
        }

        Status = accepted ? RiskAppealStatus.Accepted : RiskAppealStatus.Denied;
        DecidedBy = reviewerId;
        DecidedAt = UtcTimestamp.Normalize(now);
        DecisionNote = trimmed;
    }

    public static RiskAppealRecord Rehydrate(
        Guid id,
        Guid taskId,
        Guid ownerId,
        string? ruleCode,
        int ruleVersion,
        RiskVerdict verdict,
        string reason,
        DateTimeOffset submittedAt,
        RiskAppealStatus status,
        Guid? decidedBy,
        DateTimeOffset? decidedAt,
        string? decisionNote) =>
        new(id, taskId, ownerId, ruleCode, ruleVersion, verdict, reason, UtcTimestamp.Normalize(submittedAt), status,
            decidedBy, decidedAt is { } decided ? UtcTimestamp.Normalize(decided) : null, decisionNote);
}
