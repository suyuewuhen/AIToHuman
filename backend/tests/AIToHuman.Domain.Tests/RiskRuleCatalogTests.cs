using AIToHuman.Domain.Risk;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 禁止任务测试集：这一组用例就是“平台声称会拦住的类别”的可执行版本。
/// 只要有人放宽或删掉某条规则，这里就会红——这是禁止任务拦截能不能上线的最低门槛。
/// </summary>
public sealed class RiskRuleCatalogTests
{
    /// <summary>北京时间时区，用于把截止时间换算成“当地时间几点”。</summary>
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    private static RiskAssessment Evaluate(
        string title,
        string description = "到前台取件",
        decimal reward = 50,
        DateTimeOffset? deadline = null,
        IEnumerable<string>? criteria = null,
        string? executionAddress = null) =>
        RiskRuleCatalog.Evaluate(title, description, criteria ?? ["按时送达"], executionAddress, reward, deadline ?? new DateTimeOffset(2026, 9, 14, 9, 0, 0, ChinaOffset));

    [Theory]
    // 代考与冒名（PRD 第 10 节的验收场景）
    [InlineData("帮我代考英语四级", "prohibited.exam_impersonation")]
    [InlineData("帮我代人上课并签到", "prohibited.exam_impersonation")]
    [InlineData("帮我代写论文，包过", "prohibited.exam_impersonation")]
    [InlineData("帮我冒名去参加面试", "prohibited.exam_impersonation")]
    // 违禁品与危险品
    [InlineData("帮我取一个装毒品的包裹", "prohibited.illegal_goods")]
    [InlineData("帮我买一把管制刀具送到小区门口", "prohibited.illegal_goods")]
    [InlineData("帮我带两盒迷药过来", "prohibited.illegal_goods")]
    // 欺诈、伪造与绕过身份核验
    [InlineData("帮我破解他的微信账号", "prohibited.fraud_and_identity_bypass")]
    [InlineData("帮我查他的开房记录", "prohibited.fraud_and_identity_bypass")]
    [InlineData("帮我弄一张假证", "prohibited.fraud_and_identity_bypass")]
    // 跟踪、偷拍与骚扰
    [InlineData("帮我跟踪我前女友并且偷拍她", "prohibited.surveillance_and_harassment")]
    [InlineData("在他车上装一个定位器", "prohibited.surveillance_and_harassment")]
    [InlineData("帮我打电话骚扰一下他", "prohibited.surveillance_and_harassment")]
    // 危害人身安全
    [InlineData("找人揍他一顿", "prohibited.harm_to_person")]
    [InlineData("帮我教训一个人", "prohibited.harm_to_person")]
    // 必须由持证人员执行的医疗行为
    [InlineData("帮我开处方买安眠药", "prohibited.licensed_medical")]
    [InlineData("上门帮我输液", "prohibited.licensed_medical")]
    public void Prohibited_tasks_are_blocked_with_a_stable_reason_code(string title, string expectedCode)
    {
        var assessment = Evaluate(title);

        Assert.Equal(RiskVerdict.Blocked, assessment.Verdict);
        Assert.Equal(expectedCode, assessment.RuleCode);
        Assert.Equal(RiskRuleCatalog.Version, assessment.RuleVersion);
        Assert.False(string.IsNullOrWhiteSpace(assessment.Category));
        Assert.False(string.IsNullOrWhiteSpace(assessment.Description));
    }

    [Theory]
    // 证件与重要文件
    [InlineData("帮我把身份证从家里送到公司", "review.identity_documents")]
    [InlineData("去取一下我的营业执照原件", "review.identity_documents")]
    // 受限场所
    [InlineData("去医院帮我取检查报告", "review.restricted_venue")]
    [InlineData("去法院帮我递一份材料", "review.restricted_venue")]
    // 敏感物品与照护对象
    [InlineData("帮我把这块名表送去维修", "review.sensitive_item")]
    [InlineData("帮我取一万块现金", "review.sensitive_item")]
    // 刻意不说明内容
    [InlineData("帮我取一个包裹，不用问是什么", "review.vague_item")]
    public void Sensitive_but_not_forbidden_tasks_go_to_manual_review(string title, string expectedCode)
    {
        var assessment = Evaluate(title);

        Assert.Equal(RiskVerdict.NeedsReview, assessment.Verdict);
        Assert.Equal(expectedCode, assessment.RuleCode);
    }

    [Theory]
    [InlineData("明天下午帮我去前台取一份文件")]
    [InlineData("帮我在菜市场买两斤苹果送到家")]
    [InlineData("帮我去营业厅拍一张门口的营业时间照片")]
    [InlineData("帮我把快递从驿站取回来")]
    [InlineData("帮我去图书馆还两本书")]
    [InlineData("帮我排队买一杯奶茶")]
    [InlineData("代取文件")]
    [InlineData("代送一份合同复印件到公司前台")]
    public void Ordinary_errands_are_allowed(string title)
    {
        var assessment = Evaluate(title);

        Assert.Equal(RiskVerdict.Allowed, assessment.Verdict);
        Assert.Null(assessment.RuleCode);
    }

    [Fact]
    public void Splitting_a_keyword_with_spaces_does_not_evade_the_rules()
    {
        // 把词拆开是最省事的绕过手法，匹配前会先去掉空白字符。
        Assert.Equal(RiskVerdict.Blocked, Evaluate("帮我代 考英语四级").Verdict);
        Assert.Equal(RiskVerdict.Blocked, Evaluate("帮我代\n考英语四级").Verdict);
        Assert.Equal("prohibited.illegal_goods", Evaluate("帮我带点 毒 品").RuleCode);
    }

    [Fact]
    public void A_forbidden_category_wins_over_a_review_category()
    {
        // 同时命中“代考”和“身份证”时，结论必须是禁止发布，不能被转人工顶掉。
        var assessment = Evaluate("帮我代考，考试要带身份证");

        Assert.Equal(RiskVerdict.Blocked, assessment.Verdict);
        Assert.Equal("prohibited.exam_impersonation", assessment.RuleCode);
    }

    [Fact]
    public void Execution_address_and_acceptance_criteria_are_also_scanned()
    {
        var byAddress = RiskRuleCatalog.Evaluate("取一份材料", "按门牌号送到", ["放在前台"], "某某医院住院部", 50, new DateTimeOffset(2026, 9, 14, 9, 0, 0, ChinaOffset));
        var byCriteria = RiskRuleCatalog.Evaluate("取一份材料", "送到公司", ["必须把身份证原件交到本人手上"], null, 50, new DateTimeOffset(2026, 9, 14, 9, 0, 0, ChinaOffset));

        Assert.Equal(RiskVerdict.NeedsReview, byAddress.Verdict);
        Assert.Equal("review.restricted_venue", byAddress.RuleCode);
        Assert.Equal(RiskVerdict.NeedsReview, byCriteria.Verdict);
        Assert.Equal("review.identity_documents", byCriteria.RuleCode);
    }

    [Fact]
    public void A_high_reward_goes_to_review()
    {
        var assessment = Evaluate("帮我取一份文件", reward: RiskRuleCatalog.HighRewardThreshold + 1);

        Assert.Equal(RiskVerdict.NeedsReview, assessment.Verdict);
        Assert.Equal("review.high_reward", assessment.RuleCode);
        Assert.Contains($"{RiskRuleCatalog.HighRewardThreshold:0}", assessment.Description);
    }

    [Fact]
    public void The_threshold_itself_is_still_allowed()
    {
        Assert.Equal(RiskVerdict.Allowed, Evaluate("帮我取一份文件", reward: RiskRuleCatalog.HighRewardThreshold).Verdict);
    }

    [Fact]
    public void A_deadline_in_the_small_hours_needs_review()
    {
        // 北京时间凌晨 2 点：按当地时间判断，不受服务器时区影响。
        var assessment = Evaluate("帮我取一份文件", deadline: new DateTimeOffset(2026, 9, 14, 2, 0, 0, ChinaOffset));

        Assert.Equal(RiskVerdict.NeedsReview, assessment.Verdict);
        Assert.Equal("review.night_window", assessment.RuleCode);
    }

    [Fact]
    public void The_same_instant_written_in_utc_is_judged_the_same_way()
    {
        var beijing = Evaluate("帮我取一份文件", deadline: new DateTimeOffset(2026, 9, 14, 2, 0, 0, ChinaOffset));
        var utc = Evaluate("帮我取一份文件", deadline: new DateTimeOffset(2026, 9, 13, 18, 0, 0, TimeSpan.Zero));

        Assert.Equal(beijing.Verdict, utc.Verdict);
        Assert.Equal(beijing.RuleCode, utc.RuleCode);
    }

    [Fact]
    public void Daytime_deadlines_are_allowed()
    {
        Assert.Equal(RiskVerdict.Allowed, Evaluate("帮我取一份文件", deadline: new DateTimeOffset(2026, 9, 14, 9, 0, 0, ChinaOffset)).Verdict);
        Assert.Equal(RiskVerdict.Allowed, Evaluate("帮我取一份文件", deadline: new DateTimeOffset(2026, 9, 14, 21, 0, 0, ChinaOffset)).Verdict);
    }

    [Fact]
    public void The_catalog_is_versioned_and_well_formed()
    {
        Assert.True(RiskRuleCatalog.Version >= 1);
        // 规则目录里只允许放“禁止”和“转人工”两类；放行是默认结果，不需要规则。
        Assert.All(RiskRuleCatalog.Rules, rule => Assert.NotEqual(RiskVerdict.Allowed, rule.Verdict));
        Assert.All(RiskRuleCatalog.Rules, rule => Assert.NotEmpty(rule.Keywords));
        // 原因代码必须唯一，否则事后无法区分是哪条规则拦的。
        Assert.Equal(RiskRuleCatalog.Rules.Count, RiskRuleCatalog.Rules.Select(rule => rule.Code).Distinct(StringComparer.Ordinal).Count());
        // 禁止类别的规则必须至少有一条，否则“禁止任务拦截”名存实亡。
        Assert.Contains(RiskRuleCatalog.Rules, rule => rule.Verdict == RiskVerdict.Blocked);
    }

    [Fact]
    public void Match_words_never_single_characters_that_would_catch_ordinary_errands()
    {
        // 单字匹配（例如“代”）会把“代取”“代送”这类正常任务全部误伤，这里锁死这个约束。
        Assert.All(RiskRuleCatalog.Rules, rule => Assert.All(rule.Keywords, keyword => Assert.True(keyword.Length >= 2, $"匹配词“{keyword}”太短，容易误伤正常任务")));
    }
}
