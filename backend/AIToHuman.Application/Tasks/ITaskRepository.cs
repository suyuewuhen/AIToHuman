using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

public interface ITaskRepository
{
    /// <summary>按条件分页查询已发布任务；调用方多取一条用于判断是否还有下一页。</summary>
    IReadOnlyCollection<TaskItem> ListPublished(PublishedTaskFilter filter);

    /// <summary>按所有者返回其全部任务，包含尚未发布、已分配和已结束的状态。</summary>
    IReadOnlyCollection<TaskItem> ListByOwner(Guid ownerId);

    TaskItem? Get(Guid id);
    void Add(TaskItem task);
    void Save(TaskItem task);
}
