using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

public interface ITaskRepository
{
    /// <summary>按条件分页查询已发布任务；调用方多取一条用于判断是否还有下一页。</summary>
    IReadOnlyCollection<TaskItem> ListPublished(PublishedTaskFilter filter);

    /// <summary>按所有者返回其全部任务，包含尚未发布、已分配和已结束的状态。</summary>
    IReadOnlyCollection<TaskItem> ListByOwner(Guid ownerId);

    /// <summary>
    /// 后台过期扫描用：已过截止时间但仍处于 <c>Published</c>（无人被选中）的任务，按截止时间升序取前 <paramref name="limit"/> 条。
    /// 实现要返回被跟踪的实体，保存时才能用读到的版本做并发校验，避免两个实例重复处理同一条任务。
    /// </summary>
    IReadOnlyCollection<TaskItem> ListOverduePublished(DateTimeOffset now, int limit);

    /// <summary>服务者视角的“我的报名”：他报过名的任务（含已被处理与已撤回的记录），按报名时间倒序。</summary>
    IReadOnlyCollection<TaskItem> ListByApplicant(Guid workerId, int limit);

    /// <summary>
    /// 运营风险复核队列：处于“等人工复核”的任务，按创建时间升序取前 <paramref name="limit"/> 条（先来先处理）。
    /// 实现要返回被跟踪的实体，处置时才能用读到的版本做并发校验，避免两个运营同时处置同一条。
    /// </summary>
    IReadOnlyCollection<TaskItem> ListPendingRiskReview(int limit);

    /// <summary>
    /// 运营申诉队列：处于“待处置申诉”的任务，按提交时间升序取前 <paramref name="limit"/> 条。
    /// 与复核队列一样要返回被跟踪的实体，处置时才能用读到的版本做并发校验。
    /// </summary>
    IReadOnlyCollection<TaskItem> ListPendingRiskAppeal(int limit);

    TaskItem? Get(Guid id);
    void Add(TaskItem task);
    void Save(TaskItem task);
}
