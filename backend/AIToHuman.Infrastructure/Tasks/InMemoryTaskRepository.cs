using System.Collections.Concurrent;
using AIToHuman.Application.Admin;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Infrastructure.Tasks;

public sealed class InMemoryTaskRepository : ITaskRepository, IAdminTaskQuery
{
    private readonly ConcurrentDictionary<Guid, TaskItem> _tasks = new();

    public IReadOnlyCollection<TaskItem> ListPublished(PublishedTaskFilter filter) => _tasks.Values
        .Where(task => task.Status == Domain.Tasks.TaskStatus.Published)
        .Where(task => filter.District is null || task.District == filter.District)
        .Where(task => filter.MinReward is null || task.Reward.Amount >= filter.MinReward)
        .Where(task => filter.MaxReward is null || task.Reward.Amount <= filter.MaxReward)
        .Where(task => filter.CursorDeadline is null || filter.CursorId is null
            || task.Deadline > filter.CursorDeadline
            || (task.Deadline == filter.CursorDeadline && task.Id.CompareTo(filter.CursorId.Value) > 0))
        .OrderBy(task => task.Deadline)
        .ThenBy(task => task.Id)
        .Take(filter.Limit)
        .ToArray();
    public IReadOnlyCollection<TaskItem> ListByOwner(Guid ownerId) => _tasks.Values.Where(task => task.OwnerId == ownerId).OrderByDescending(task => task.CreatedAt).ToArray();
    public TaskItem? Get(Guid id) => _tasks.GetValueOrDefault(id);

    public void Add(TaskItem task)
    {
        if (!_tasks.TryAdd(task.Id, task)) throw new InvalidOperationException("任务标识冲突。");
    }

    public void Save(TaskItem task) => _tasks[task.Id] = task;

    /// <summary>运营检索：跨所有者，按关键字匹配标题/描述/区域，可选状态过滤。</summary>
    public IReadOnlyCollection<TaskItem> Search(string? keyword, TaskStatus? status, int limit)
    {
        var trimmed = keyword?.Trim();
        return _tasks.Values
            .Where(task => status is null || task.Status == status)
            .Where(task => string.IsNullOrEmpty(trimmed)
                || task.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || task.Description.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
                || task.District.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(task => task.CreatedAt)
            .ThenBy(task => task.Id)
            .Take(limit)
            .ToArray();
    }
}
