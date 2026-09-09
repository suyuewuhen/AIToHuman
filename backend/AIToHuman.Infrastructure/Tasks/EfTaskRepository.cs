using System.Text.Json;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Infrastructure.Tasks;

public sealed class EfTaskRepository(TaskDbContext db) : ITaskRepository
{
    public IReadOnlyCollection<TaskItem> ListPublished() => db.Tasks
        .AsNoTracking()
        .Include(task => task.Applications)
        .Where(task => task.Status == nameof(DomainTaskStatus.Published))
        .OrderBy(task => task.Deadline)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public TaskItem? Get(Guid id) => db.Tasks
        .AsNoTracking()
        .Include(task => task.Applications)
        .SingleOrDefault(task => task.Id == id) is { } record ? Map(record) : null;

    public void Add(TaskItem task)
    {
        db.Tasks.Add(ToRecord(task));
        db.SaveChanges();
    }

    public void Save(TaskItem task)
    {
        var record = db.Tasks.Single(item => item.Id == task.Id);
        record.Status = task.Status.ToString();
        record.RewardAmount = task.Reward.Amount;
        record.RewardCurrency = task.Reward.Currency;
        var storedApplications = db.Applications.Where(item => item.TaskId == task.Id).ToDictionary(item => item.Id);
        foreach (var current in task.Applications)
        {
            if (storedApplications.TryGetValue(current.Id, out var existing))
            {
                existing.Status = current.Status.ToString();
                existing.Note = current.Note;
                existing.SubmittedAt = current.SubmittedAt;
            }
            else
            {
                db.Applications.Add(ToRecord(current, task.Id));
            }
        }
        db.SaveChanges();
    }

    private static TaskRecord ToRecord(TaskItem task) => new()
    {
        Id = task.Id, OwnerId = task.OwnerId, Title = task.Title, Description = task.Description, District = task.District,
        Deadline = task.Deadline, RewardAmount = task.Reward.Amount, RewardCurrency = task.Reward.Currency,
        Status = task.Status.ToString(), AcceptanceCriteriaJson = JsonSerializer.Serialize(task.AcceptanceCriteria), CreatedAt = task.CreatedAt,
        Applications = task.Applications.Select(item => ToRecord(item, task.Id)).ToList()
    };

    private static ApplicationRecord ToRecord(TaskApplication item, Guid taskId) => new()
    {
        Id = item.Id, TaskId = taskId, WorkerId = item.WorkerId, Note = item.Note, SubmittedAt = item.SubmittedAt, Status = item.Status.ToString()
    };

    private static TaskItem Map(TaskRecord record) => TaskItem.Rehydrate(
        record.Id, record.OwnerId, record.Title, record.Description, record.District, record.Deadline,
        new Money(record.RewardAmount, record.RewardCurrency), JsonSerializer.Deserialize<string[]>(record.AcceptanceCriteriaJson) ?? [], record.CreatedAt,
        Enum.Parse<DomainTaskStatus>(record.Status), record.Applications.Select(item => TaskApplication.Rehydrate(item.Id, item.WorkerId, item.Note, item.SubmittedAt, Enum.Parse<TaskApplicationStatus>(item.Status))));
}
