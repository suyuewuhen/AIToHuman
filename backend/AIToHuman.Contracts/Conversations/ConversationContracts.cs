using AIToHuman.Contracts.Tasks;

namespace AIToHuman.Contracts.Conversations;

public sealed record CreateConversationRequest(Guid? UserId);

public sealed record ConversationMessageResponse(Guid Id, string Role, string Content, DateTimeOffset CreatedAt, bool ReadyToDraft, AiTaskPlanResponse? Plan);

public sealed record ConversationResponse(Guid Id, Guid UserId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<ConversationMessageResponse> Messages, AiTaskPlanResponse? Draft);

public sealed record ConversationSummaryResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int MessageCount, string Preview, bool HasDraft);
