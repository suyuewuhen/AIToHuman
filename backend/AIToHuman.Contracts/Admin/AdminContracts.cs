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

/// <summary>
/// 运营风险复核队列里的一条任务：命中的规则（原因代码 + 类别 + 说明 + 规则版本）与任务原文一并返回，
/// 运营不必再跳到别处去凑上下文。
/// </summary>
public sealed record AdminRiskReviewItemResponse(
    Guid TaskId,
    string Title,
    string Description,
    string District,
    decimal RewardAmount,
    string RewardCurrency,
    DateTimeOffset Deadline,
    DateTimeOffset CreatedAt,
    Guid OwnerId,
    string? OwnerDisplayName,
    string? OwnerEmail,
    string Verdict,
    string? RuleCode,
    string? Category,
    string? Summary,
    int RuleVersion,
    DateTimeOffset? AssessedAt,
    string ReviewStatus,
    string TaskStatus,
    DateTimeOffset? ReviewedAt,
    string? ReviewNote,
    Guid? ReviewedBy);

public sealed record AdminRiskReviewListResponse(IReadOnlyList<AdminRiskReviewItemResponse> Items, int Limit);

/// <summary>
/// 处置风险复核：<c>decision</c> 取 <c>Approve</c>（放行，之后可发布）或 <c>Reject</c>（驳回，不能发布），依据必填。
/// 被禁止的类别不会进这个队列，人工也无权放行。
/// </summary>
public sealed record AdminRiskReviewDecisionRequest(string Decision, string Note);

/// <summary>
/// 运营看到的待处置申诉。原始风险结论与申诉理由一并返回，运营据此判断是不是误伤；
/// <paramref name="CanBeReleasedByAppeal"/> 为 false 表示这是禁止类别命中——
/// 申诉成立也只会记录结论，不会让任务获得发布许可。
/// </summary>
public sealed record AdminRiskAppealItemResponse(
    Guid TaskId,
    string Title,
    string Description,
    string District,
    decimal RewardAmount,
    string RewardCurrency,
    Guid OwnerId,
    string? OwnerEmail,
    string Verdict,
    string? RuleCode,
    string? Category,
    string? Summary,
    int RuleVersion,
    string ReviewStatus,
    string? ReviewNote,
    string AppealStatus,
    string? AppealReason,
    DateTimeOffset? AppealedAt,
    bool CanBeReleasedByAppeal);

public sealed record AdminRiskAppealListResponse(IReadOnlyList<AdminRiskAppealItemResponse> Items, int Limit);

/// <summary>处置申诉：<c>decision</c> 取 <c>Accept</c>（认为误伤）或 <c>Deny</c>（维持原判），依据必填。</summary>
public sealed record AdminRiskAppealDecisionRequest(string Decision, string Note);

/// <summary>风险规则目录的自述：版本、规则条数与规则清单，供运营后台说明“现在按什么规则拦”。</summary>
public sealed record RiskRuleResponse(string Code, string Category, string Verdict, string Description, int KeywordCount);

public sealed record RiskRuleCatalogResponse(int Version, decimal HighRewardThreshold, IReadOnlyList<RiskRuleResponse> Rules);
