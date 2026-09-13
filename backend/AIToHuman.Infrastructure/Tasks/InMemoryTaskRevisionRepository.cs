using System.Collections.Concurrent;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Infrastructure.Tasks;

/// <summary>内存实现：与 EF 一样只追加，版本号按任务取最大值。</summary>
public sealed class InMemoryTaskRevisionRepository : ITaskRevisionRepository
{
    private readonly ConcurrentDictionary<Guid, List<TaskDraftRevision>> _revisions = new();

    public IReadOnlyCollection<TaskDraftRevision> ListByTask(Guid taskId) =>
        _revisions.TryGetValue(taskId, out var items)
            ? items.OrderBy(item => item.Revision).ToArray()
            : [];

    public int LatestRevision(Guid taskId) =>
        _revisions.TryGetValue(taskId, out var items) && items.Count > 0 ? items.Max(item => item.Revision) : 0;

    public void Add(TaskDraftRevision revision)
    {
        var items = _revisions.GetOrAdd(revision.TaskId, _ => []);
        lock (items)
        {
            items.Add(revision);
        }
    }
}
