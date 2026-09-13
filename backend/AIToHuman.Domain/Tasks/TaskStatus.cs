namespace AIToHuman.Domain.Tasks;

public enum TaskStatus
{
    ReadyToPublish,
    Published,
    Assigned,
    Closed,
    Expired,
    Cancelled
}

/// <summary>订单被取消后任务的去向，供调用方决定要不要再发通知。</summary>
public enum TaskReleaseOutcome
{
    /// <summary>还没到截止时间，任务回到大厅重新招募。</summary>
    Reopened,

    /// <summary>已经过了截止时间，任务直接过期。</summary>
    Expired
}
