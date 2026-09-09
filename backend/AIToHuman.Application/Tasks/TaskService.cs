using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Application.Tasks;

public sealed class TaskService(ITaskRepository repository, TimeProvider timeProvider)
{
    public TaskResponse Create(CreateTaskRequest request)
    {
        var task = new TaskItem(request.OwnerId, request.Title, request.Description, request.District, request.Deadline, new Money(request.Reward), request.AcceptanceCriteria, timeProvider.GetUtcNow());
        repository.Add(task);
        return Map(task);
    }

    public TaskResponse Publish(Guid id, Guid ownerId)
    {
        var task = GetOwned(id, ownerId);
        task.Publish(timeProvider.GetUtcNow());
        repository.Save(task);
        return Map(task);
    }

    public TaskResponse IncreaseReward(Guid id, Guid ownerId, IncreaseRewardRequest request)
    {
        var task = GetOwned(id, ownerId);
        task.IncreaseReward(new Money(request.Reward));
        repository.Save(task);
        return Map(task);
    }

    public TaskResponse Apply(Guid id, ApplyForTaskRequest request)
    {
        var task = GetRequired(id);
        task.Apply(request.WorkerId, request.Note, timeProvider.GetUtcNow());
        repository.Save(task);
        return Map(task);
    }

    public TaskResponse Select(Guid id, Guid applicationId, SelectApplicationRequest request)
    {
        var task = GetOwned(id, request.OwnerId);
        task.SelectApplication(applicationId);
        repository.Save(task);
        return Map(task);
    }

    public IReadOnlyCollection<TaskApplicationResponse> ListApplications(Guid id, Guid ownerId)
    {
        var task = GetOwned(id, ownerId);
        return task.Applications.Select(MapApplication).ToArray();
    }

    public IReadOnlyCollection<TaskSummaryResponse> ListPublished() => repository.ListPublished().Select(MapSummary).ToArray();
    public TaskResponse? Get(Guid id) => repository.Get(id) is { } task ? Map(task) : null;
    public TaskSummaryResponse? GetPublic(Guid id) => repository.Get(id) is { } task ? MapSummary(task) : null;

    public RewardSuggestionResponse SuggestReward(RewardSuggestionRequest request)
    {
        var travel = (decimal)Math.Max(request.DistanceKilometers, 0) * 2.2m;
        var labor = Math.Max(request.EstimatedMinutes, 15) / 60m * 24m;
        var peak = request.IsPeakHours ? 8m : 0m;
        var category = request.Category.Equals("queue", StringComparison.OrdinalIgnoreCase) ? 10m : 5m;
        var suggested = Math.Ceiling(Math.Max(20m, travel + labor + peak + category) / 5m) * 5m;
        return new(suggested, Math.Max(15m, suggested - 10m), suggested + 20m, "CNY", ["预计距离", "预计耗时", request.IsPeakHours ? "高峰时段" : "普通时段", "任务类别"], "cold-start");
    }

    private TaskItem GetOwned(Guid id, Guid ownerId)
    {
        var task = GetRequired(id);
        if (task.OwnerId != ownerId) throw new UnauthorizedAccessException("只有任务所有者可以执行该操作。");
        return task;
    }

    private TaskItem GetRequired(Guid id) => repository.Get(id) ?? throw new KeyNotFoundException("任务不存在。");

    private static TaskResponse Map(TaskItem task) => new(task.Id, task.OwnerId, task.Title, task.Description, task.District, task.Deadline, task.Reward.Amount, task.Reward.Currency, task.Status.ToString(), task.AcceptanceCriteria, task.Applications.Select(MapApplication).ToArray());

    private static TaskApplicationResponse MapApplication(TaskApplication application) => new(application.Id, application.WorkerId, application.Note, application.Status.ToString(), application.SubmittedAt);

    private static TaskSummaryResponse MapSummary(TaskItem task) => new(task.Id, task.OwnerId, task.Title, task.Description, task.District, task.Deadline, task.Reward.Amount, task.Reward.Currency, task.Status.ToString(), task.AcceptanceCriteria, task.Applications.Count);
}
