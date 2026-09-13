namespace AIToHuman.Domain.Risk;

/// <summary>
/// 误拦申诉的状态。
///
/// 申诉不是"翻案快捷键"：它把"我认为规则判错了"这条诉求送到运营面前，由人给一个结论。
/// 处置能力分两档（见 <see cref="Tasks.TaskItem.ResolveRiskAppeal"/>）：
/// 转人工被驳回的任务可以申诉成立并放行；被禁止类别命中的任务即使申诉成立，
/// 也**不会**因此获得发布许可——那是平台红线，人工无权放行，申诉成立的结论只用于记录误伤并提示改文案。
/// </summary>
public enum RiskAppealStatus
{
    /// <summary>没有申诉，或者申诉已被更后面的编辑作废。</summary>
    None,

    /// <summary>已提交，等运营处置。</summary>
    Pending,

    /// <summary>运营认为误判：转人工的会放行；禁止类别的只记录结论。</summary>
    Accepted,

    /// <summary>运营维持原判。</summary>
    Denied
}
