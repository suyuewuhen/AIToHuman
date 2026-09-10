using System.Collections.Concurrent;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Infrastructure.Tasks;

public sealed class InMemoryTaskRepository : ITaskRepository
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
}
