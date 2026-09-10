using AIToHuman.Api;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Conversations;

namespace AIToHuman.IntegrationTests;

/// <summary>模型输出的严格校验规则。</summary>
public sealed class AiTaskPlanValidatorTests
{
    [Fact]
    public void Accepts_a_complete_plan_and_normalizes_text()
    {
        var turn = new AiConversationTurnResponse("  信息已经齐全。  ".Trim(), true, Plan(title: "  代取文件  ", district: "  浦东新区  ", reward: 60.126m));

        var validated = AiTaskPlanValidator.Validate(turn, 0);

        Assert.True(validated.ReadyToDraft);
        Assert.Equal("代取文件", validated.Plan!.Title);
        Assert.Equal("浦东新区", validated.Plan.District);
        Assert.Equal(60.13m, validated.Plan.SuggestedReward);
    }

    [Fact]
    public void Rejects_empty_assistant_message()
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("   ", false, null), 7));

        Assert.Contains("没有返回可显示的回复", error.Message);
        Assert.Equal(7, error.VisibleLength);
    }

    [Fact]
    public void Rejects_assistant_message_longer_than_the_conversation_limit()
    {
        var message = new string('你', ConversationLimits.MaxMessageLength + 1);

        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse(message, false, null), 0));

        Assert.Contains("回复过长", error.Message);
    }

    [Fact]
    public void Drops_plan_when_not_ready_to_draft()
    {
        // 模型偶尔在未标记 readyToDraft 时也带上 plan，这里必须丢弃，避免前端提前解锁草稿。
        var validated = AiTaskPlanValidator.Validate(new AiConversationTurnResponse("还差一个地点。", false, Plan()), 0);

        Assert.False(validated.ReadyToDraft);
        Assert.Null(validated.Plan);
    }

    [Fact]
    public void Rejects_ready_to_draft_without_plan()
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以生成了。", true, null), 0));

        Assert.Contains("没有返回任务草稿", error.Message);
    }

    [Theory]
    [InlineData("", "缺少任务标题")]
    [InlineData("   ", "缺少任务标题")]
    public void Rejects_missing_title(string title, string expected)
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以生成了。", true, Plan(title: title)), 0));

        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void Rejects_title_longer_than_eighty_characters()
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以生成了。", true, Plan(title: new string('题', 81))), 0));

        Assert.Contains("标题超过 80 个字符", error.Message);
    }

    [Fact]
    public void Rejects_missing_description_or_district()
    {
        Assert.Contains("缺少任务描述", Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以。", true, Plan(description: " ")), 0)).Message);
        Assert.Contains("缺少区域", Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以。", true, Plan(district: "")), 0)).Message);
    }

    [Fact]
    public void Rejects_plan_without_acceptance_criteria()
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以。", true, Plan(criteria: [" ", ""])), 0));

        Assert.Contains("缺少验收标准", error.Message);
    }

    [Fact]
    public void Rejects_over_long_acceptance_criterion()
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以。", true, Plan(criteria: [new string('要', 201)])), 0));

        Assert.Contains("验收标准超过 200 个字符", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100001)]
    public void Rejects_unusable_suggested_reward(decimal reward)
    {
        var error = Assert.Throws<AiPlanningFormatException>(() => AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以。", true, Plan(reward: reward)), 0));

        Assert.Contains("建议悬赏", error.Message);
    }

    [Fact]
    public void Caps_criteria_and_clarifications_instead_of_failing()
    {
        var plan = Plan(criteria: ["一", "二", "三", "四", "五", "六", "七"]) with
        {
            Clarifications = ["1", "2", "3", "4", "5", "6", "7", "8"]
        };

        var validated = AiTaskPlanValidator.Validate(new AiConversationTurnResponse("可以。", true, plan), 0);

        Assert.Equal(AiTaskPlanValidator.MaxAcceptanceCriteria, validated.Plan!.AcceptanceCriteria.Count);
        Assert.Equal(AiTaskPlanValidator.MaxClarifications, validated.Plan.Clarifications.Count);
    }

    private static AiTaskPlanResponse Plan(
        string title = "代取文件",
        string description = "到前台取一份普通文件",
        string district = "浦东新区",
        decimal reward = 50m,
        string[]? criteria = null) =>
        new(title, description, district, new DateTimeOffset(2026, 9, 12, 7, 0, 0, TimeSpan.Zero), criteria ?? ["上传取件码照片"], reward, [], "volcengine");
}
