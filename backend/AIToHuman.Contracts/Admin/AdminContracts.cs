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

/// <summary>
/// 运营看到的订单（含争议信息）。<paramref name="DisputeResolution"/> 为空表示还没处置。
/// 提交说明与凭证说明一并返回，方便运营在处置前看清双方交了什么。
/// </summary>
public sealed record AdminOrderItemResponse(
    Guid Id,
    Guid TaskId,
    string Title,
    string Status,
    Guid OwnerId,
    string? OwnerEmail,
    Guid WorkerId,
    string? WorkerEmail,
    decimal RewardAmount,
    string RewardCurrency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt,
    string? EvidenceNote,
    string? ReviewNote,
    string? RejectionNote,
    int ReworkCount,
    DateTimeOffset? CancelledAt,
    Guid? CancelledBy,
    string? CancellationReason,
    string? DisputeReason,
    Guid? DisputeOpenedBy,
    DateTimeOffset? DisputeOpenedAt,
    string? DisputeResolution,
    string? DisputeResolutionNote,
    DateTimeOffset? DisputeResolvedAt);

public sealed record AdminOrderListResponse(IReadOnlyList<AdminOrderItemResponse> Items, int Limit);

/// <summary>处置争议：<c>decision</c> 取 <c>Approve</c>/<c>Rework</c>/<c>Cancel</c>，依据必填。</summary>
public sealed record AdminResolveDisputeRequest(string Decision, string Note);

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
