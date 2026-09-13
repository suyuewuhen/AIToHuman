namespace AIToHuman.Domain.Risk;

/// <summary>
/// 人工复核状态：只有 <see cref="RiskVerdict.NeedsReview"/> 的任务才会进入
/// <see cref="Pending"/>，由运营放行或驳回后固定在终态。
/// </summary>
public enum RiskReviewStatus
{
    /// <summary>规则直接放行，不需要人工介入。</summary>
    NotRequired,

    /// <summary>已进人工队列，等运营处置；期间不能发布。</summary>
    Pending,

    /// <summary>运营放行，之后可以发布。</summary>
    Approved,

    /// <summary>运营驳回，不能发布（任务可以自己撤销）。</summary>
    Rejected
}
