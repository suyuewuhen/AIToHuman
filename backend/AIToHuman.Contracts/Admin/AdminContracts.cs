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
    Guid? ReviewedBy,
    string EnforcementStatus = "None",
    string? EnforcementReason = null,
    DateTimeOffset? EnforcedAt = null);

public sealed record AdminRiskReviewListResponse(IReadOnlyList<AdminRiskReviewItemResponse> Items, int Limit);

/// <summary>
/// 手动触发一轮发布后风险复检的结果：扫了多少条、各处置结果多少条。
/// <paramref name="RuleVersion"/> 是本轮用的规则版本，<paramref name="Skipped"/> 是留给下一轮的（订单不可冻结等）。
/// </summary>
public sealed record AdminRiskRecheckResponse(
    int RuleVersion,
    int Scanned,
    int Refreshed,
    int Flagged,
    int Unpublished,
    int Frozen,
    int Skipped);

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
    bool CanBeReleasedByAppeal,
    int AppealCount = 0);

/// <summary>一次申诉的留档：提交理由、提交时的规则与结论、运营结论与依据。</summary>
public sealed record AdminRiskAppealRecordResponse(
    Guid Id,
    string? RuleCode,
    int RuleVersion,
    string Verdict,
    string Reason,
    DateTimeOffset SubmittedAt,
    string Status,
    Guid? DecidedBy,
    string? DecidedByName,
    DateTimeOffset? DecidedAt,
    string? DecisionNote);

/// <summary>
/// 某条任务的申诉轨迹。带上当前生效的节流上限，运营据此判断"这个人是不是已经在刷申诉"。
/// </summary>
public sealed record AdminRiskAppealHistoryResponse(
    Guid TaskId,
    IReadOnlyList<AdminRiskAppealRecordResponse> Items,
    int MaxPerTask,
    int MaxPerOwnerPerDay);

public sealed record AdminRiskAppealListResponse(IReadOnlyList<AdminRiskAppealItemResponse> Items, int Limit);

/// <summary>处置申诉：<c>decision</c> 取 <c>Accept</c>（认为误伤）或 <c>Deny</c>（维持原判），依据必填。</summary>
public sealed record AdminRiskAppealDecisionRequest(string Decision, string Note);

/// <summary>风险规则目录的自述：版本、规则条数与规则清单，供运营后台说明“现在按什么规则拦”。</summary>
public sealed record RiskRuleResponse(string Code, string Category, string Verdict, string Description, int KeywordCount);

public sealed record RiskRuleCatalogResponse(int Version, decimal HighRewardThreshold, IReadOnlyList<RiskRuleResponse> Rules);

/// <summary>规则明细：连匹配词一起给出来，只对运营开放（普通接口只给词的数量，避免用户逐字试探绕过）。</summary>
public sealed record RiskRuleDetailResponse(string Code, string Category, string Verdict, string Description, IReadOnlyList<string> Keywords);

/// <summary>
/// 当前生效的规则目录明细。<c>IsBuiltIn</c> 为真表示还没有任何运营覆盖（用的就是代码里的内置目录，版本 1）；
/// <c>ChangeSummary</c>/<c>ChangeReason</c>/<c>UpdatedBy</c>/<c>UpdatedAt</c> 描述最近一版改动的来龙去脉。
/// </summary>
public sealed record RiskRuleCatalogDetailResponse(
    int Version,
    bool IsBuiltIn,
    decimal HighRewardThreshold,
    string NightWindowStart,
    string NightWindowEnd,
    IReadOnlyList<RiskRuleDetailResponse> Rules,
    string? ChangeSummary,
    string? ChangeReason,
    Guid? UpdatedBy,
    string? UpdatedByName,
    DateTimeOffset? UpdatedAt);

/// <summary>编辑一版规则：整份目录替换（规则 + 阈值 + 时段）。<c>ExpectedVersion</c> 用来挡住“拿着旧目录覆盖别人刚改的内容”。</summary>
public sealed record UpdateRiskRuleCatalogRequest(
    int? ExpectedVersion,
    string Reason,
    decimal HighRewardThreshold,
    string NightWindowStart,
    string NightWindowEnd,
    IReadOnlyList<RiskRuleDetailRequest> Rules);

public sealed record RiskRuleDetailRequest(string Code, string Category, string Verdict, string Description, IReadOnlyList<string> Keywords);

/// <summary>把目录恢复到代码内置的那一版（同样是追加一版新版本，历史不丢）。依据必填。</summary>
public sealed record ResetRiskRuleCatalogRequest(int? ExpectedVersion, string Reason);

/// <summary>版本历史里的一条：只看改动摘要与依据，不看当时的完整词表（要看某一版就查那一版明细）。</summary>
public sealed record RiskRuleCatalogVersionResponse(
    int Version,
    string ChangeSummary,
    string ChangeReason,
    Guid UpdatedBy,
    string? UpdatedByName,
    DateTimeOffset CreatedAt,
    int RuleCount,
    int BlockedRuleCount,
    decimal HighRewardThreshold,
    string NightWindowStart,
    string NightWindowEnd);

public sealed record RiskRuleCatalogVersionListResponse(IReadOnlyList<RiskRuleCatalogVersionResponse> Items, int Limit);
