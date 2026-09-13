namespace AIToHuman.Contracts.Tasks;

public sealed record CreateTaskRequest(Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, IReadOnlyList<string> AcceptanceCriteria, string? ExecutionAddress = null, DateTimeOffset? ApplicationDeadline = null);
public sealed record IncreaseRewardRequest(decimal Reward);
public sealed record ApplyForTaskRequest(Guid WorkerId, string? Note);
public sealed record SelectApplicationRequest(Guid OwnerId);

/// <summary>服务者撤回自己尚未被处理的报名；撤回后可以重新报名。</summary>
public sealed record WithdrawApplicationRequest(Guid WorkerId);

/// <summary>所有者撤销自己的任务；原因必填，会记在任务上并通知报名中的服务者。</summary>
public sealed record CancelTaskRequest(Guid OwnerId, string Reason);

public sealed record TaskApplicationResponse(Guid Id, Guid WorkerId, string Note, string Status, DateTimeOffset SubmittedAt);
public sealed record TaskResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<TaskApplicationResponse> Applications, DateTimeOffset? ExpiredAt = null, DateTimeOffset? CancelledAt = null, string? CancellationReason = null, DateTimeOffset? ApplicationDeadline = null, bool AcceptingApplications = false, string RiskVerdict = "Allowed", string? RiskRuleCode = null, string? RiskCategory = null, string? RiskSummary = null, int RiskRuleVersion = 0, DateTimeOffset? RiskAssessedAt = null, string RiskReviewStatus = "NotRequired", DateTimeOffset? RiskReviewedAt = null, string? RiskReviewNote = null, bool RiskPublishBlocked = false);
public sealed record OrderResponse(Guid Id, Guid TaskId, Guid OwnerId, Guid WorkerId, string Title, decimal Reward, string Currency, string Status, DateTimeOffset CreatedAt, string? EvidenceNote, string? ReviewNote, DateTimeOffset? SubmittedAt, DateTimeOffset? ReviewedAt, int ReworkCount, string? RejectionNote, int UnreadMessageCount = 0, DateTimeOffset? CancelledAt = null, Guid? CancelledBy = null, string? CancellationReason = null, string? DisputeReason = null, Guid? DisputeOpenedBy = null, DateTimeOffset? DisputeOpenedAt = null, string? DisputeResult = null, string? DisputeResolutionNote = null, DateTimeOffset? DisputeResolvedAt = null);
public sealed record OrderActionRequest(Guid ActorId, string? Note);
public sealed record CreateReviewRequest(Guid ReviewerId, int Rating, string? Comment);
public sealed record ReviewResponse(Guid Id, Guid OrderId, Guid ReviewerId, Guid RevieweeId, int Rating, string Comment, DateTimeOffset CreatedAt, bool IsVisible);
public sealed record ReviewSummaryResponse(Guid UserId, decimal AverageRating, int ReviewCount, IReadOnlyCollection<ReviewResponse> RecentReviews);
public sealed record AiConversationMessage(string Role, string Content);
public sealed record AiTaskPlanRequest(Guid ConversationId, Guid? UserId, string Message);
public sealed record AiTaskPlanResponse(string Title, string Description, string District, DateTimeOffset Deadline, IReadOnlyList<string> AcceptanceCriteria, decimal SuggestedReward, IReadOnlyList<string> Clarifications, string Provider);
public sealed record AiConversationTurnResponse(string AssistantMessage, bool ReadyToDraft, AiTaskPlanResponse? Plan);
public sealed record SelectTaskResult(TaskResponse Task, OrderResponse Order);
public sealed record TaskSummaryResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, int ApplicationCount, bool HasExecutionAddress = false, DateTimeOffset? ExpiredAt = null, string? CancellationReason = null, DateTimeOffset? ApplicationDeadline = null, bool AcceptingApplications = false, string RiskVerdict = "Allowed", string? RiskRuleCode = null, string? RiskCategory = null, string? RiskSummary = null, int RiskRuleVersion = 0, string RiskReviewStatus = "NotRequired", string? RiskReviewNote = null, bool RiskPublishBlocked = false);

/// <summary>
/// 服务者视角的一条报名记录（“我的报名”列表）。<paramref name="CanWithdraw"/> 由服务端判定：
/// 只有待处理、且任务还没被选中别人时才为 true，避免客户端自己推算状态。
/// </summary>
public sealed record MyApplicationResponse(
    Guid ApplicationId,
    Guid TaskId,
    string Title,
    string District,
    decimal Reward,
    string Currency,
    DateTimeOffset Deadline,
    DateTimeOffset? ApplicationDeadline,
    string TaskStatus,
    string ApplicationStatus,
    DateTimeOffset SubmittedAt,
    bool CanWithdraw);

public sealed record MyApplicationListResponse(IReadOnlyList<MyApplicationResponse> Items);

/// <summary>大厅列表查询：按区域与悬赏区间筛选，游标分页，按截止时间升序。</summary>
public sealed record TaskSearchRequest(string? District, decimal? MinReward, decimal? MaxReward, int Limit, string? Cursor);

public sealed record TaskListResponse(IReadOnlyList<TaskSummaryResponse> Items, string? NextCursor, bool HasMore);

/// <summary>精确执行地址；只有所有者与被选中的服务者能拿到值，其他人得到 403。</summary>
public sealed record TaskExecutionAddressResponse(Guid TaskId, string? ExecutionAddress);
public sealed record RewardSuggestionRequest(string Category, double DistanceKilometers, int EstimatedMinutes, bool IsPeakHours);
public sealed record RewardSuggestionResponse(decimal SuggestedReward, decimal MinimumReward, decimal MaximumReward, string Currency, IReadOnlyList<string> Factors, string DataConfidence);
