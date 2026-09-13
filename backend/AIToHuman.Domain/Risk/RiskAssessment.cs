namespace AIToHuman.Domain.Risk;

/// <summary>
/// 一次风险判定的结果：结论 + 命中的规则 + 可展示的说明 + 规则版本。
/// 结果会原样记在任务上，所以它必须自洽到“事后只看这条记录也能解释当时的决定”。
/// </summary>
public sealed record RiskAssessment(RiskVerdict Verdict, string? RuleCode, string? Category, string? Description, int RuleVersion)
{
    public static RiskAssessment Allowed(int ruleVersion) => new(RiskVerdict.Allowed, null, null, null, ruleVersion);
}
