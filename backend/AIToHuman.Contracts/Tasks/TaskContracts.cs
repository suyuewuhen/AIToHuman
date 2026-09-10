namespace AIToHuman.Contracts.Tasks;

public sealed record CreateTaskRequest(Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, IReadOnlyList<string> AcceptanceCriteria);
public sealed record IncreaseRewardRequest(decimal Reward);
public sealed record ApplyForTaskRequest(Guid WorkerId, string? Note);
public sealed record SelectApplicationRequest(Guid OwnerId);
public sealed record TaskApplicationResponse(Guid Id, Guid WorkerId, string Note, string Status, DateTimeOffset SubmittedAt);
public sealed record TaskResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<TaskApplicationResponse> Applications);
public sealed record OrderResponse(Guid Id, Guid TaskId, Guid OwnerId, Guid WorkerId, string Title, decimal Reward, string Currency, string Status, DateTimeOffset CreatedAt, string? EvidenceNote, string? ReviewNote, DateTimeOffset? SubmittedAt, DateTimeOffset? ReviewedAt);
public sealed record OrderActionRequest(Guid ActorId, string? Note);
public sealed record CreateReviewRequest(Guid ReviewerId, int Rating, string? Comment);
public sealed record ReviewResponse(Guid Id, Guid OrderId, Guid ReviewerId, Guid RevieweeId, int Rating, string Comment, DateTimeOffset CreatedAt, bool IsVisible);
public sealed record ReviewSummaryResponse(Guid UserId, decimal AverageRating, int ReviewCount, IReadOnlyCollection<ReviewResponse> RecentReviews);
public sealed record SelectTaskResult(TaskResponse Task, OrderResponse Order);
public sealed record TaskSummaryResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, int ApplicationCount);
public sealed record RewardSuggestionRequest(string Category, double DistanceKilometers, int EstimatedMinutes, bool IsPeakHours);
public sealed record RewardSuggestionResponse(decimal SuggestedReward, decimal MinimumReward, decimal MaximumReward, string Currency, IReadOnlyList<string> Factors, string DataConfidence);
