namespace AIToHuman.Contracts.Tasks;

public sealed record CreateTaskRequest(Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, IReadOnlyList<string> AcceptanceCriteria, string? ExecutionAddress = null);
public sealed record IncreaseRewardRequest(decimal Reward);
public sealed record ApplyForTaskRequest(Guid WorkerId, string? Note);
public sealed record SelectApplicationRequest(Guid OwnerId);
public sealed record TaskApplicationResponse(Guid Id, Guid WorkerId, string Note, string Status, DateTimeOffset SubmittedAt);
public sealed record TaskResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<TaskApplicationResponse> Applications);
public sealed record OrderResponse(Guid Id, Guid TaskId, Guid OwnerId, Guid WorkerId, string Title, decimal Reward, string Currency, string Status, DateTimeOffset CreatedAt, string? EvidenceNote, string? ReviewNote, DateTimeOffset? SubmittedAt, DateTimeOffset? ReviewedAt, int ReworkCount, string? RejectionNote, int UnreadMessageCount = 0);
public sealed record OrderActionRequest(Guid ActorId, string? Note);
public sealed record CreateReviewRequest(Guid ReviewerId, int Rating, string? Comment);
public sealed record ReviewResponse(Guid Id, Guid OrderId, Guid ReviewerId, Guid RevieweeId, int Rating, string Comment, DateTimeOffset CreatedAt, bool IsVisible);
public sealed record ReviewSummaryResponse(Guid UserId, decimal AverageRating, int ReviewCount, IReadOnlyCollection<ReviewResponse> RecentReviews);
public sealed record AiConversationMessage(string Role, string Content);
public sealed record AiTaskPlanRequest(Guid ConversationId, Guid? UserId, string Message);
public sealed record AiTaskPlanResponse(string Title, string Description, string District, DateTimeOffset Deadline, IReadOnlyList<string> AcceptanceCriteria, decimal SuggestedReward, IReadOnlyList<string> Clarifications, string Provider);
public sealed record AiConversationTurnResponse(string AssistantMessage, bool ReadyToDraft, AiTaskPlanResponse? Plan);
public sealed record SelectTaskResult(TaskResponse Task, OrderResponse Order);
public sealed record TaskSummaryResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, int ApplicationCount, bool HasExecutionAddress = false);

/// <summary>大厅列表查询：按区域与悬赏区间筛选，游标分页，按截止时间升序。</summary>
public sealed record TaskSearchRequest(string? District, decimal? MinReward, decimal? MaxReward, int Limit, string? Cursor);

public sealed record TaskListResponse(IReadOnlyList<TaskSummaryResponse> Items, string? NextCursor, bool HasMore);

/// <summary>精确执行地址；只有所有者与被选中的服务者能拿到值，其他人得到 403。</summary>
public sealed record TaskExecutionAddressResponse(Guid TaskId, string? ExecutionAddress);
public sealed record RewardSuggestionRequest(string Category, double DistanceKilometers, int EstimatedMinutes, bool IsPeakHours);
public sealed record RewardSuggestionResponse(decimal SuggestedReward, decimal MinimumReward, decimal MaximumReward, string Currency, IReadOnlyList<string> Factors, string DataConfidence);
