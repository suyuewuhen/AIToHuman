using AIToHuman.Domain.Risk;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 风险判定留档的存储：只追加，不更新也不删除。
/// 读取有两条路：按任务看"这条任务被判定过几次、每次什么结论"，以及按时间窗口做聚合统计。
/// </summary>
public interface IRiskDecisionRepository
{
    void Add(RiskDecisionEntry entry);

    /// <summary>某条任务的全部判定留痕，按时间升序。</summary>
    IReadOnlyCollection<RiskDecisionEntry> ListByTask(Guid taskId, int limit);

    /// <summary>按时间窗口取原始留痕（统计口径都在应用层算，两种实现保持一致）。</summary>
    IReadOnlyCollection<RiskDecisionEntry> ListSince(DateTimeOffset since, int limit);
}

/// <summary>申诉留档里的"误伤"统计：按原因代码数一数有多少次申诉被认定为误伤。</summary>
public interface IRiskAppealStatistics
{
    /// <summary>某个时间点之后被认定为误伤（<c>Accepted</c>）的申诉次数，按原因代码分组。</summary>
    IReadOnlyDictionary<string, int> CountAcceptedByRuleCode(DateTimeOffset since);
}
