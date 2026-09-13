using AIToHuman.Domain.Risk;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 误拦申诉的留档存储：一次申诉一行，只追加；结论写回同一行。
/// 读取要同时支持"看某条任务的申诉轨迹"与"统计某个人一天提交了多少次"（节流用）。
/// </summary>
public interface IRiskAppealRepository
{
    /// <summary>新增一条申诉（提交时调用）。</summary>
    void Add(RiskAppealRecord record);

    /// <summary>把结论写回已有的一行。</summary>
    void Save(RiskAppealRecord record);

    /// <summary>某条任务的全部申诉记录，按提交时间升序。</summary>
    IReadOnlyCollection<RiskAppealRecord> ListByTask(Guid taskId);

    /// <summary>这条任务累计申诉了多少次（节流用：包括已经处置过的）。</summary>
    int CountByTask(Guid taskId);

    /// <summary>批量取多条任务的累计申诉次数（运营队列一次查完，避免每条各查一遍）。</summary>
    IReadOnlyDictionary<Guid, int> CountByTasks(IReadOnlyCollection<Guid> taskIds);

    /// <summary>某个人在 <paramref name="since"/> 之后提交了多少次申诉（节流用）。</summary>
    int CountByOwnerSince(Guid ownerId, DateTimeOffset since);

    /// <summary>某条任务上仍在等处置的申诉（正常情况下最多一条）。</summary>
    RiskAppealRecord? FindPending(Guid taskId);
}
