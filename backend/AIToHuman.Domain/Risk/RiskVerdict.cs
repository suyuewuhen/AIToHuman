namespace AIToHuman.Domain.Risk;

/// <summary>
/// 确定性风险规则对一条任务的结论。
/// <see cref="Allowed"/> 可以正常发布；<see cref="NeedsReview"/> 要等运营人工复核放行；
/// <see cref="Blocked"/> 一律不能发布（平台禁止的任务类别）。
/// </summary>
public enum RiskVerdict
{
    Allowed,
    NeedsReview,
    Blocked
}
