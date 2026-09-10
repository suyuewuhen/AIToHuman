using System.Text.Json;
using AIToHuman.Application.Tasks;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using DomainTaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Infrastructure.Tasks;

public sealed class EfTaskRepository(TaskDbContext db) : ITaskRepository
{
    public IReadOnlyCollection<TaskItem> ListPublished(PublishedTaskFilter filter)
    {
        var query = db.Tasks
            .AsNoTracking()
            .Include(task => task.Applications)
            .Where(task => task.Status == nameof(DomainTaskStatus.Published));

        if (filter.District is { } district) query = query.Where(task => task.District == district);
        if (filter.MinReward is { } minReward) query = query.Where(task => task.RewardAmount >= minReward);
        if (filter.MaxReward is { } maxReward) query = query.Where(task => task.RewardAmount <= maxReward);
        if (filter.CursorDeadline is { } deadline && filter.CursorId is { } cursorId)
        {
            // 与排序键保持一致：先比截止时间，同一时刻再用 Id 兜底，避免分页丢条或重复。
            query = query.Where(task => task.Deadline > deadline || (task.Deadline == deadline && task.Id.CompareTo(cursorId) > 0));
        }

        return query
            .OrderBy(task => task.Deadline)
            .ThenBy(task => task.Id)
            .Take(filter.Limit)
            .AsEnumerable()
            .Select(Map)
            .ToArray();
    }

    public IReadOnlyCollection<TaskItem> ListByOwner(Guid ownerId) => db.Tasks
        .AsNoTracking()
        .Include(task => task.Applications)
        .Where(task => task.OwnerId == ownerId)
        .OrderByDescending(task => task.CreatedAt)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    // 注意：Get 必须保持跟踪状态。乐观并发令牌依赖「做业务判断时读到的版本」与
    // Save 时 WHERE 里用的原始版本是同一个；一旦这里用 AsNoTracking，Save 会重新读库拿到
    // 最新版本再自增，并发写入就永远不会冲突。
    public TaskItem? Get(Guid id) => db.Tasks
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

        // 自增并发令牌：EF 会用加载时的原值做 WHERE，并发写入时后到者拿到 0 行更新并抛冲突。
        record.Version += 1;
        db.SaveChanges();
    }

    private static TaskRecord ToRecord(TaskItem task) => new()
    {
        Id = task.Id, OwnerId = task.OwnerId, Title = task.Title, Description = task.Description, District = task.District,
        ExecutionAddress = task.ExecutionAddress,
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
        Enum.Parse<DomainTaskStatus>(record.Status), record.Applications.Select(item => TaskApplication.Rehydrate(item.Id, item.WorkerId, item.Note, item.SubmittedAt, Enum.Parse<TaskApplicationStatus>(item.Status))), record.ExecutionAddress);
}
