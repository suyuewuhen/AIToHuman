using System.Collections.Concurrent;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Infrastructure.Tasks;

public sealed class InMemoryTaskRepository : ITaskRepository
{
    private readonly ConcurrentDictionary<Guid, TaskItem> _tasks = new();

    public IReadOnlyCollection<TaskItem> ListPublished() => _tasks.Values.Where(task => task.Status == Domain.Tasks.TaskStatus.Published).OrderBy(task => task.Deadline).ToArray();
    public TaskItem? Get(Guid id) => _tasks.GetValueOrDefault(id);

    public void Add(TaskItem task)
    {
        if (!_tasks.TryAdd(task.Id, task)) throw new InvalidOperationException("任务标识冲突。");
    }
}
