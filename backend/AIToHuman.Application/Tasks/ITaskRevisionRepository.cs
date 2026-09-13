using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

/// <summary>草稿版本历史：只追加，按版本号升序读。</summary>
public interface ITaskRevisionRepository
{
    /// <summary>某个任务的全部历史版本，按版本号升序。</summary>
    IReadOnlyCollection<TaskDraftRevision> ListByTask(Guid taskId);

    /// <summary>当前最大版本号；没有任何版本时返回 0。</summary>
    int LatestRevision(Guid taskId);

    void Add(TaskDraftRevision revision);
}
