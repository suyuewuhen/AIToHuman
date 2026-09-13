namespace AIToHuman.Domain.Risk;

/// <summary>
/// 一条规则的落库形态。结论以字符串保存（<c>Blocked</c>/<c>NeedsReview</c>），
/// 这样以后往枚举里加值也不会让历史快照读不出来。
/// </summary>
public sealed record RiskRuleSnapshot(
    string Code,
    string Category,
    string Verdict,
    string Description,
    IReadOnlyList<string> Keywords);

/// <summary>
/// 一版风险规则目录的完整快照（规则 + 阈值 + 时段 + 版本号）。
/// 存完整快照而不是“增量补丁”，是为了让任何一版都能独立还原：
/// 复盘一条历史拦截时，直接把那一版快照喂回 <see cref="RiskRuleCatalog.FromJson"/> 就能重放当时的判定。
/// </summary>
public sealed record RiskRuleCatalogSnapshot(
    int Version,
    decimal HighRewardThreshold,
    string NightWindowStart,
    string NightWindowEnd,
    IReadOnlyList<RiskRuleSnapshot> Rules);
