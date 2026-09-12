namespace AIToHuman.Contracts.Admin;

/// <summary>运营后台看到的任务摘要（跨所有者）。</summary>
public sealed record AdminTaskItemResponse(
    Guid Id,
    string Title,
    string District,
    string Status,
    decimal RewardAmount,
    string RewardCurrency,
    DateTimeOffset Deadline,
    DateTimeOffset CreatedAt,
    Guid OwnerId,
    string? OwnerDisplayName,
    string? OwnerEmail,
    int ApplicationCount,
    bool HasExecutionAddress,
    Guid? OrderId,
    string? OrderStatus);

public sealed record AdminTaskListResponse(IReadOnlyList<AdminTaskItemResponse> Items, int Limit);

public sealed record AdminTaskApplicationResponse(
    Guid Id,
    Guid WorkerId,
    string? WorkerDisplayName,
    string Status,
    string Note,
    DateTimeOffset SubmittedAt);

public sealed record AdminTaskDetailResponse(
    AdminTaskItemResponse Task,
    string Description,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<AdminTaskApplicationResponse> Applications);

/// <summary>人工下架必须给原因，原因会写进运营审计。</summary>
public sealed record AdminCancelTaskRequest(string Reason);

public sealed record AdminUserResponse(Guid Id, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt);

public sealed record AdminUserListResponse(IReadOnlyList<AdminUserResponse> Items, int Limit);

public sealed record AdminAuditResponse(
    Guid Id,
    Guid ActorId,
    string Action,
    string TargetType,
    Guid TargetId,
    string Reason,
    DateTimeOffset OccurredAt);
