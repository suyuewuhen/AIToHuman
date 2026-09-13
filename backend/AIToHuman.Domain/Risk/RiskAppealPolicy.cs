namespace AIToHuman.Domain.Risk;

/// <summary>
/// 误拦申诉的节流口径。
///
/// 为什么需要它：领域规则已经保证"同一版内容只能申诉一次"，但所有人只要改一个字就能再申诉一次，
/// 于是"改一个字 → 再申诉"可以无限刷运营队列——这跟刷单是同一类问题，必须在服务端设上限。
/// 两条上限分工不同：<see cref="MaxPerTask"/> 管住单条任务的纠缠，
/// <see cref="MaxPerOwnerPerDay"/> 管住一个人拿多条任务刷屏。
/// </summary>
public static class RiskAppealPolicy
{
    /// <summary>同一条任务累计最多申诉多少次（跨版本累计，改文案也算一次）。</summary>
    public const int MaxPerTask = 3;

    /// <summary>同一个人一天最多提交多少次申诉。</summary>
    public const int MaxPerOwnerPerDay = 5;

    /// <summary>频率上限的统计窗口。</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(1);
}
