namespace AIToHuman.Contracts.Tasks;

public sealed record CreateTaskRequest(Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, IReadOnlyList<string> AcceptanceCriteria);
public sealed record IncreaseRewardRequest(decimal Reward);
public sealed record ApplyForTaskRequest(Guid WorkerId, string? Note);
public sealed record SelectApplicationRequest(Guid OwnerId);
public sealed record TaskApplicationResponse(Guid Id, Guid WorkerId, string Note, string Status, DateTimeOffset SubmittedAt);
public sealed record TaskResponse(Guid Id, Guid OwnerId, string Title, string Description, string District, DateTimeOffset Deadline, decimal Reward, string Currency, string Status, IReadOnlyList<string> AcceptanceCriteria, IReadOnlyList<TaskApplicationResponse> Applications);
public sealed record RewardSuggestionRequest(string Category, double DistanceKilometers, int EstimatedMinutes, bool IsPeakHours);
public sealed record RewardSuggestionResponse(decimal SuggestedReward, decimal MinimumReward, decimal MaximumReward, string Currency, IReadOnlyList<string> Factors, string DataConfidence);
