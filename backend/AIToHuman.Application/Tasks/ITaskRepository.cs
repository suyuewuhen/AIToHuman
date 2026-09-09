using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

public interface ITaskRepository
{
    IReadOnlyCollection<TaskItem> ListPublished();
    TaskItem? Get(Guid id);
    void Add(TaskItem task);
    void Save(TaskItem task);
}
