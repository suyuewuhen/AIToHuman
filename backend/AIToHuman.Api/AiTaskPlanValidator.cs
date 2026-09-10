using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Conversations;

namespace AIToHuman.Api;

/// <summary>
/// 对模型返回的一轮结果做严格校验：字段齐全、长度合法、草稿字段满足后续创建任务的领域约束。
/// 校验不通过抛 <see cref="AiPlanningFormatException"/>，由 <see cref="AiPlanningService"/> 判断能否安全重试。
/// </summary>
public static class AiTaskPlanValidator
{
    public const int MaxTitleLength = 80;
    public const int MinAcceptanceCriteria = 1;
    public const int MaxAcceptanceCriteria = 6;
    public const int MaxCriterionLength = 200;
    public const int MaxClarifications = 6;
    public const int MaxClarificationLength = 120;
    public const decimal MaxSuggestedReward = 100000m;

    /// <summary>校验并规范化一轮结果。<paramref name="visibleLength"/> 是已经发给用户的可见字数，用于判断重试是否安全。</summary>
    public static AiConversationTurnResponse Validate(AiConversationTurnResponse turn, int visibleLength)
    {
        if (string.IsNullOrWhiteSpace(turn.AssistantMessage))
            throw new AiPlanningFormatException("AI 没有返回可显示的回复，请重试。", visibleLength);
        if (turn.AssistantMessage.Length > ConversationLimits.MaxMessageLength)
            throw new AiPlanningFormatException("AI 返回的回复过长，请重试。", visibleLength);

        if (!turn.ReadyToDraft)
        {
            // 未标记需求明确时，模型偶尔会多带一个 plan；这里一律丢弃，避免前端提前解锁草稿。
            return new AiConversationTurnResponse(turn.AssistantMessage, false, null);
        }

        var plan = turn.Plan ?? throw new AiPlanningFormatException("AI 标记需求已明确，但没有返回任务草稿，请重试。", visibleLength);

        if (string.IsNullOrWhiteSpace(plan.Title))
            throw new AiPlanningFormatException("AI 返回的草稿缺少任务标题，请重试。", visibleLength);
        if (plan.Title.Trim().Length > MaxTitleLength)
            throw new AiPlanningFormatException($"AI 返回的任务标题超过 {MaxTitleLength} 个字符，请重试。", visibleLength);
        if (string.IsNullOrWhiteSpace(plan.Description))
            throw new AiPlanningFormatException("AI 返回的草稿缺少任务描述，请重试。", visibleLength);
        if (string.IsNullOrWhiteSpace(plan.District))
            throw new AiPlanningFormatException("AI 返回的草稿缺少区域或线上说明，请重试。", visibleLength);

        var criteria = plan.AcceptanceCriteria
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToArray();
        if (criteria.Length < MinAcceptanceCriteria)
            throw new AiPlanningFormatException("AI 返回的草稿缺少验收标准，请重试。", visibleLength);
        if (criteria.Any(item => item.Length > MaxCriterionLength))
            throw new AiPlanningFormatException($"AI 返回的验收标准超过 {MaxCriterionLength} 个字符，请重试。", visibleLength);

        if (plan.SuggestedReward <= 0)
            throw new AiPlanningFormatException("AI 返回的建议悬赏不是有效金额，请重试。", visibleLength);
        if (plan.SuggestedReward > MaxSuggestedReward)
            throw new AiPlanningFormatException("AI 返回的建议悬赏明显超出合理范围，请重试。", visibleLength);

        var clarifications = plan.Clarifications
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().Length <= MaxClarificationLength ? item.Trim() : item.Trim()[..MaxClarificationLength])
            .Take(MaxClarifications)
            .ToArray();

        // 超出上限的验收标准只截断，不重试：内容本身是合法的，少几条不影响用户确认。
        var normalized = new AiTaskPlanResponse(
            plan.Title.Trim(),
            plan.Description.Trim(),
            plan.District.Trim(),
            plan.Deadline,
            criteria.Take(MaxAcceptanceCriteria).ToArray(),
            Math.Round(plan.SuggestedReward, 2),
            clarifications,
            plan.Provider);

        return new AiConversationTurnResponse(turn.AssistantMessage, true, normalized);
    }
}
