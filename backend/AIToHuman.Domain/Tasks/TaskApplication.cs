namespace AIToHuman.Domain.Tasks;

public enum TaskApplicationStatus
{
    Pending,
    Selected,
    Withdrawn,
    Rejected,
    Expired
}

public sealed class TaskApplication
{
    internal TaskApplication(Guid workerId, string note, DateTimeOffset submittedAt)
    {
        Id = Guid.NewGuid();
        WorkerId = workerId;
        Note = note.Trim();
        SubmittedAt = submittedAt;
    }

    public Guid Id { get; private set; }
    public Guid WorkerId { get; }
    public string Note { get; }
    public DateTimeOffset SubmittedAt { get; }
    public TaskApplicationStatus Status { get; internal set; } = TaskApplicationStatus.Pending;

    public static TaskApplication Rehydrate(Guid id, Guid workerId, string note, DateTimeOffset submittedAt, TaskApplicationStatus status)
    {
        return new TaskApplication(workerId, note, submittedAt)
        {
            Id = id,
            Status = status
        };
    }
}
