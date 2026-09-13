using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 风险判定的留痕与统计：把"每一次判定"固定下来，并回答"哪条规则拦了多少、误伤多少"。
///
/// 记录点覆盖所有会跑判定的动作（创建、编辑、回滚、发布、加价、发布后复检）——
/// 少记一个点，统计就会出现"看不见的那部分"，而这恰恰是最容易出问题的地方。
/// 统计口径刻意简单：窗口内按原因代码分组数命中次数，再叠加同窗口内"申诉被认定为误伤"的次数，
/// 于是每一条规则都能看到"拦了多少 / 其中多少被判成误伤"。
/// </summary>
public sealed class RiskDecisionService(
    IRiskDecisionRepository decisions,
    IRiskAppealStatistics appeals)
{
    /// <summary>统计窗口的默认与上限（天）。</summary>
    public const int DefaultWindowDays = 30;
    public const int MaxWindowDays = 365;

    /// <summary>一次最多返回多少条原始留痕。</summary>
    public const int MaxListLimit = 200;

    /// <summary>记一条判定留痕。调用方负责放在与状态变更同一个工作单元里。</summary>
    public void Record(TaskItem task, RiskDecisionReason reason, DateTimeOffset now) =>
        decisions.Add(RiskDecisionEntry.Record(task, reason, now));

    /// <summary>某条任务的判定留痕（按时间升序），运营排查"这条任务为什么被拦"时用。</summary>
    public IReadOnlyCollection<RiskDecisionEntryResponse> ListByTask(Guid taskId, int? limit) =>
        decisions.ListByTask(taskId, Math.Clamp(limit ?? MaxListLimit, 1, MaxListLimit)).Select(Map).ToArray();

    /// <summary>
    /// 命中统计：窗口内的判定总数、按结论的分布、以及按（原因代码 + 结论）分组的明细，
    /// 每条规则带上窗口内被认定为误伤的次数——这就是规则调参的依据。
    /// </summary>
    public RiskDecisionStatsResponse Summarize(int? days, DateTimeOffset now)
    {
        var window = Math.Clamp(days ?? DefaultWindowDays, 1, MaxWindowDays);
        var since = now.AddDays(-window);
        var entries = decisions.ListSince(since, 20000);
        var accepted = appeals.CountAcceptedByRuleCode(since);

        var rules = entries
            .Where(entry => entry.RuleCode is not null)
            .GroupBy(entry => (entry.RuleCode!, entry.Verdict))
            .Select(group => new RiskRuleHitResponse(
                group.Key.Item1,
                group.Select(item => item.Category).FirstOrDefault(category => category is not null) ?? "(未记录类别)",
                group.Key.Item2.ToString(),
                group.Count(),
                group.Count(item => item.Reason == RiskDecisionReason.Rechecked),
                group.Min(item => item.OccurredAt),
                group.Max(item => item.OccurredAt),
                accepted.GetValueOrDefault(group.Key.Item1)))
            .OrderByDescending(item => item.Hits)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();

        return new RiskDecisionStatsResponse(
            since,
            now,
            window,
            entries.Count,
            entries.Count(item => item.Verdict == RiskVerdict.Allowed),
            entries.Count(item => item.Verdict == RiskVerdict.NeedsReview),
            entries.Count(item => item.Verdict == RiskVerdict.Blocked),
            entries.Count(item => item.Reason == RiskDecisionReason.Rechecked),
            rules);
    }

    private static RiskDecisionEntryResponse Map(RiskDecisionEntry entry) => new(
        entry.Id,
        entry.TaskId,
        entry.Reason.ToString(),
        entry.Verdict.ToString(),
        entry.RuleCode,
        entry.Category,
        entry.RuleVersion,
        entry.RewardAmount,
        entry.OccurredAt);
}
