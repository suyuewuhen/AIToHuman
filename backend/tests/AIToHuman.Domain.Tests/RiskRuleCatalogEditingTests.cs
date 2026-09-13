using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 运营编辑风险规则的领域约束：目录能不能被改成不合法/不安全的样子，以及
/// "改动要留档"这件事的底线（版本号必须 +1、依据必填、内容真变了才记一版）。
/// 这些约束是后台编辑功能的安全边界，所以全部落在领域层、由用例锁死。
/// </summary>
public sealed class RiskRuleCatalogEditingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private static readonly Guid AdminId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void A_catalog_must_keep_at_least_one_blocked_rule()
    {
        // 禁止类别是平台的硬门禁：一次误操作删光就再也补不回来，所以这一条必须留在目录里。
        var exception = Assert.Throws<DomainException>(() => Catalog(rules: [Rule("review.only", RiskVerdict.NeedsReview, "身份证")]));

        Assert.Contains("至少要保留一条禁止类规则", exception.Message);
    }

    [Fact]
    public void Reason_codes_must_be_unique()
    {
        var exception = Assert.Throws<DomainException>(() => Catalog(rules: [Rule("prohibited.a"), Rule("prohibited.a")]));

        Assert.Contains("重复", exception.Message);
    }

    [Fact]
    public void A_single_character_match_word_is_rejected()
    {
        // 单字匹配词会把“代取”“代送”这类正常任务一起误伤，内置目录一直守着这条线，运营也不能越过去。
        var exception = Assert.Throws<DomainException>(() => Catalog(rules: [Rule(keywords: ["代"])]));

        Assert.Contains("至少 2 个字", exception.Message);
    }

    [Fact]
    public void An_over_long_match_word_is_rejected()
    {
        var exception = Assert.Throws<DomainException>(() => Catalog(rules: [Rule(keywords: [new string('长', RiskRuleCatalog.MaxKeywordLength + 1)])]));

        Assert.Contains("超过", exception.Message);
    }

    [Fact]
    public void Threshold_rule_codes_are_reserved()
    {
        // review.high_reward / review.night_window 是阈值规则的代码，被普通规则顶掉会让"是阈值拦的还是词表拦的"说不清。
        var exception = Assert.Throws<DomainException>(() => Catalog(rules: [Rule(RiskRuleCatalog.HighRewardRuleCode)]));

        Assert.Contains("保留代码", exception.Message);
    }

    [Fact]
    public void A_rule_can_never_be_recorded_as_allowed()
    {
        // 目录里有一条正常的禁止规则，另一条被写成“放行”：后者必须被拒（放行是默认结果，不该由规则表达）。
        var exception = Assert.Throws<DomainException>(() => Catalog(
            rules: [Rule("prohibited.ok"), Rule("review.miswritten", RiskVerdict.Allowed, "身份证")]));

        Assert.Contains("不能是“放行”", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_000_000)]
    public void The_high_reward_threshold_must_stay_in_range(decimal threshold)
    {
        Assert.Throws<DomainException>(() => Catalog(reward: threshold));
    }

    [Fact]
    public void The_night_window_must_be_a_forward_interval()
    {
        // 终点不晚于起点等于没有深夜时段，属于明显的配置错误，不该悄悄放行。
        Assert.Throws<DomainException>(() => Catalog(start: "06:00", end: "06:00"));
        Assert.Throws<DomainException>(() => Catalog(start: "22:00", end: "06:00"));
    }

    [Fact]
    public void The_version_must_be_positive()
    {
        Assert.Throws<DomainException>(() => Catalog(version: 0));
    }

    [Fact]
    public void Custom_rules_change_the_verdict_of_the_same_text()
    {
        // 同一条文本在两版目录下结论不同——这就是"后台改规则"要能真的改变行为的最小证据。
        var custom = Catalog(
            rules: [Rule("prohibited.no_drones", keywords: ["无人机"])],
            reward: 8000m);

        var allowed = RiskRuleCatalog.BuiltIn.Evaluate("帮我把无人机送到郊区", "送到指定地点", ["完好无损"], null, 60, Now.AddHours(6));
        var blocked = custom.Evaluate("帮我把无人机送到郊区", "送到指定地点", ["完好无损"], null, 60, Now.AddHours(6));

        Assert.Equal(RiskVerdict.Allowed, allowed.Verdict);
        Assert.Equal(RiskVerdict.Blocked, blocked.Verdict);
        Assert.Equal("prohibited.no_drones", blocked.RuleCode);
        Assert.Equal(2, blocked.RuleVersion);

        // 阈值规则跟着目录走：8000 元阈值下 6000 元的任务不再转人工。
        Assert.Equal(RiskVerdict.Allowed, custom.Evaluate("帮我取一份文件", "到前台取件", ["按时送达"], null, 6000, Now.AddHours(6)).Verdict);
        Assert.Equal(RiskVerdict.NeedsReview, RiskRuleCatalog.BuiltIn.Evaluate("帮我取一份文件", "到前台取件", ["按时送达"], null, 6000, Now.AddHours(6)).Verdict);
    }

    [Fact]
    public void The_night_window_follows_the_edited_catalog()
    {
        var custom = Catalog(start: "01:00", end: "05:00");
        var deadline = new DateTimeOffset(2026, 9, 14, 0, 30, 0, ChinaOffset);

        // 内置目录把 00:30 当深夜，收窄到 [01:00,05:00) 之后就不算了。
        Assert.Equal(RiskVerdict.NeedsReview, RiskRuleCatalog.BuiltIn.Evaluate("帮我取一份文件", "到前台取件", ["按时送达"], null, 50, deadline).Verdict);
        Assert.Equal(RiskVerdict.Allowed, custom.Evaluate("帮我取一份文件", "到前台取件", ["按时送达"], null, 50, deadline).Verdict);
    }

    [Fact]
    public void A_snapshot_round_trips_through_json()
    {
        var custom = Catalog(rules: [Rule("prohibited.no_drones", keywords: ["无人机", "穿越机"])], reward: 8000m, start: "01:00", end: "05:00");

        var restored = RiskRuleCatalog.FromJson(custom.ToJson());

        Assert.Equal(custom.Version, restored.Version);
        Assert.Equal(custom.HighRewardThreshold, restored.HighRewardThreshold);
        Assert.Equal(custom.NightWindowStart, restored.NightWindowStart);
        Assert.Equal(custom.NightWindowEnd, restored.NightWindowEnd);
        Assert.Equal(custom.Rules.Select(rule => rule.Code), restored.Rules.Select(rule => rule.Code));
        Assert.Equal(["无人机", "穿越机"], restored.Rules[0].Keywords);
        Assert.Equal(custom.Evaluate("帮我把无人机送到郊区", "送到指定地点", ["完好无损"], null, 60, Now.AddHours(6)).RuleCode,
            restored.Evaluate("帮我把无人机送到郊区", "送到指定地点", ["完好无损"], null, 60, Now.AddHours(6)).RuleCode);
    }

    [Fact]
    public void A_corrupt_snapshot_fails_loudly_instead_of_falling_back_to_another_rule_set()
    {
        // 读不出规则时宁可让写入失败：静默换一套规则判定，等于悄悄改了门禁的松紧。
        Assert.Throws<InvalidOperationException>(() => RiskRuleCatalog.FromJson("{ 这不是 JSON"));
        Assert.Throws<InvalidOperationException>(() => RiskRuleCatalog.FromJson("""{"version":2,"highRewardThreshold":5000,"nightWindowStart":"00:00","nightWindowEnd":"06:00","rules":[{"code":"a","category":"b","verdict":"Something","description":"c","keywords":["代考"]}]}"""));
        Assert.Throws<InvalidOperationException>(() => RiskRuleCatalog.FromJson("""{"version":2,"highRewardThreshold":5000,"nightWindowStart":"25:00","nightWindowEnd":"06:00","rules":[]}"""));
    }

    [Fact]
    public void Window_text_round_trips_and_bad_values_are_rejected()
    {
        Assert.Equal("00:00", RiskRuleCatalog.FormatWindow(TimeSpan.Zero));
        Assert.Equal("06:00", RiskRuleCatalog.FormatWindow(TimeSpan.FromHours(6)));
        Assert.Equal("24:00", RiskRuleCatalog.FormatWindow(TimeSpan.FromDays(1)));

        Assert.True(RiskRuleCatalog.TryParseWindow("00:00", out var start, out _));
        Assert.Equal(TimeSpan.Zero, start);
        Assert.True(RiskRuleCatalog.TryParseWindow("23:59", out var late, out _));
        Assert.Equal(TimeSpan.FromMinutes((23 * 60) + 59), late);

        Assert.False(RiskRuleCatalog.TryParseWindow("24:30", out _, out _));
        Assert.False(RiskRuleCatalog.TryParseWindow("6", out _, out _));
        Assert.False(RiskRuleCatalog.TryParseWindow("", out _, out _));
        Assert.False(RiskRuleCatalog.TryParseWindow("上午", out _, out _));
    }

    [Fact]
    public void A_revision_requires_an_actor_and_a_reason()
    {
        var catalog = Catalog();

        Assert.Throws<DomainException>(() => RiskRuleCatalogRevision.Record(catalog, RiskRuleCatalog.BuiltIn, "调整词表", Guid.Empty, Now));
        Assert.Throws<DomainException>(() => RiskRuleCatalogRevision.Record(catalog, RiskRuleCatalog.BuiltIn, "   ", AdminId, Now));
        Assert.Throws<DomainException>(() => RiskRuleCatalogRevision.Record(catalog, RiskRuleCatalog.BuiltIn, new string('因', RiskRuleCatalogRevision.MaxReasonLength + 1), AdminId, Now));
    }

    [Fact]
    public void A_revision_must_bump_the_version_by_exactly_one()
    {
        var skipped = Catalog(version: 5);

        var exception = Assert.Throws<DomainException>(() => RiskRuleCatalogRevision.Record(skipped, RiskRuleCatalog.BuiltIn, "跳版本", AdminId, Now));

        Assert.Contains("大 1", exception.Message);
    }

    [Fact]
    public void An_unchanged_catalog_produces_no_revision()
    {
        // 内容没变就不该记一版：否则历史里会堆一串"什么都没改"的版本，反而看不清哪次改动生效在哪。
        var sameAsBuiltIn = new RiskRuleCatalog(
            2, RiskRuleCatalog.BuiltIn.Rules, RiskRuleCatalog.BuiltIn.HighRewardThreshold,
            RiskRuleCatalog.BuiltIn.NightWindowStart, RiskRuleCatalog.BuiltIn.NightWindowEnd);

        var exception = Assert.Throws<DomainException>(() => RiskRuleCatalogRevision.Record(sameAsBuiltIn, RiskRuleCatalog.BuiltIn, "重新保存一遍", AdminId, Now));

        Assert.Contains("没有变化", exception.Message);
    }

    [Fact]
    public void The_change_summary_spells_out_what_changed()
    {
        // 拿内置目录当基准，改三处：删一条规则、给某条规则加词、把高金额阈值抬高。
        var edited = RiskRuleCatalog.BuiltIn.Rules.Where(rule => rule.Code != "review.vague_item").ToArray();
        var target = Array.FindIndex(edited, rule => rule.Code == "prohibited.illegal_goods");
        var illegalGoods = edited[target];
        edited[target] = new RiskRule(illegalGoods.Code, illegalGoods.Category, illegalGoods.Verdict, illegalGoods.Description, [.. illegalGoods.Keywords, "笑气"]);

        var catalog = new RiskRuleCatalog(2, edited, 8000m, RiskRuleCatalog.BuiltIn.NightWindowStart, RiskRuleCatalog.BuiltIn.NightWindowEnd);
        var revision = RiskRuleCatalogRevision.Record(catalog, RiskRuleCatalog.BuiltIn, "收紧违禁品词表并抬高人工阈值", AdminId, Now);

        Assert.Contains("删除规则 1 条（review.vague_item）", revision.ChangeSummary);
        Assert.Contains("新增匹配词 1 个", revision.ChangeSummary);
        Assert.Contains("高金额阈值 5000 → 8000 元", revision.ChangeSummary);
        Assert.Equal("收紧违禁品词表并抬高人工阈值", revision.ChangeReason);
        Assert.Equal(2, revision.Version);
        // 摘要本身不写匹配词：它会进运营审计，词表只在规则明细接口按需给出。
        Assert.DoesNotContain("笑气", revision.ChangeSummary);
    }

    private static RiskRule Rule(
        string code = "prohibited.custom",
        RiskVerdict verdict = RiskVerdict.Blocked,
        params string[] keywords) =>
        new(code, "自定义类别", verdict, "自定义说明：命中后按结论处置。", keywords.Length == 0 ? ["代考"] : keywords);

    private static RiskRuleCatalog Catalog(
        int version = 2,
        IEnumerable<RiskRule>? rules = null,
        decimal reward = 5000m,
        string start = "00:00",
        string end = "06:00")
    {
        Assert.True(RiskRuleCatalog.TryParseWindow(start, out var nightStart, out _));
        Assert.True(RiskRuleCatalog.TryParseWindow(end, out var nightEnd, out _));
        return new RiskRuleCatalog(version, rules ?? [Rule()], reward, nightStart, nightEnd);
    }
}
