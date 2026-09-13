using System.Text.Json;
using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Risk;

/// <summary>
/// 确定性风险规则目录：平台禁止或需要人工审核的任务类别。
///
/// 目录有两层身份：
/// 一是代码里的**内置目录**（<see cref="BuiltIn"/>，版本 1），任何部署都自带，数据库里还没有运营覆盖时用的就是它；
/// 二是运营在后台编辑出来的目录（版本 ≥ 2），以只追加的版本快照落库，每一版都记着谁在什么时候、凭什么改的。
///
/// 为什么先做确定性规则而不是让模型判断：模型输出只能当建议，禁止类别的判定必须可复现、可回归、
/// 可解释，才能在“为什么这条任务被拦了”时有据可查（见 docs/security/security-and-risk.md 第 2、3 节）。
/// 规则只做两类动作：<see cref="RiskVerdict.Blocked"/> 一律不能发布，
/// <see cref="RiskVerdict.NeedsReview"/> 进人工队列等运营放行。
///
/// 版本号随每次编辑 +1，判定时会把当时的版本号记在任务上，
/// 因此任何一条拦截结论都能回溯到“当时用的是哪一版规则”（见 <see cref="RiskAssessment.RuleVersion"/>）。
/// </summary>
public sealed class RiskRuleCatalog
{
    /// <summary>内置目录的版本号。运营在后台编辑出来的第一版是 <c>2</c>，历史记录里的 <c>1</c> 指的就是这份内置目录。</summary>
    public const int BuiltInVersion = 1;

    /// <summary>内置的高金额阈值（元）：超过它转人工，避免大额任务直接进大厅。</summary>
    public const decimal BuiltInHighRewardThreshold = 5000m;

    /// <summary>一次目录编辑最多允许有多少条规则：目录要人能读懂，不是词库。</summary>
    public const int MaxRuleCount = 50;

    /// <summary>一条规则最多多少个匹配词。</summary>
    public const int MaxKeywordCount = 400;

    /// <summary>单个匹配词的长度上限（下限是 2，见 <see cref="RiskRule"/> 的说明）。</summary>
    public const int MaxKeywordLength = 20;

    /// <summary>规则说明与类别名的长度上限，避免后台把目录写成长文。</summary>
    public const int MaxCategoryLength = 60;
    public const int MaxDescriptionLength = 300;
    public const int MaxCodeLength = 80;

    /// <summary>阈值规则的保留原因代码：运营不能拿普通规则顶掉它们。</summary>
    public const string HighRewardRuleCode = "review.high_reward";
    public const string NightWindowRuleCode = "review.night_window";

    /// <summary>深夜时段按北京时间计算，内置值区间为 [00:00, 06:00)。</summary>
    public static readonly TimeSpan BuiltInNightWindowStart = TimeSpan.Zero;
    public static readonly TimeSpan BuiltInNightWindowEnd = TimeSpan.FromHours(6);

    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 建一份目录。所有取值都在这里校验（版本号、原因代码、匹配词长度、阈值、时段），
    /// 因为目录会被落成"某一版的快照"：写进去的每个字都要能被后来的判定复现，脏数据必须挡在写入之前。
    /// </summary>
    public RiskRuleCatalog(
        int version,
        IEnumerable<RiskRule> rules,
        decimal highRewardThreshold,
        TimeSpan nightWindowStart,
        TimeSpan nightWindowEnd)
    {
        if (version < 1) throw new DomainException("风险规则版本号必须大于 0。");

        var normalized = (rules ?? throw new DomainException("风险规则目录不能为空。")).ToArray();
        EnsureRules(normalized);
        EnsureThresholds(highRewardThreshold, nightWindowStart, nightWindowEnd);

        Version = version;
        Rules = normalized;
        HighRewardThreshold = highRewardThreshold;
        NightWindowStart = nightWindowStart;
        NightWindowEnd = nightWindowEnd;
    }

    /// <summary>规则目录版本。改规则（增删词、调整阈值、调整结论）时递增，历史判定据此回溯。</summary>
    public int Version { get; }

    public IReadOnlyList<RiskRule> Rules { get; }

    /// <summary>高金额阈值（元）：超过它转人工。</summary>
    public decimal HighRewardThreshold { get; }

    /// <summary>深夜时段起点（北京时间，含）。</summary>
    public TimeSpan NightWindowStart { get; }

    /// <summary>深夜时段终点（北京时间，不含）。</summary>
    public TimeSpan NightWindowEnd { get; }

    /// <summary>目录里禁止类（一律不能发布）的规则条数。</summary>
    public int BlockedRuleCount => Rules.Count(rule => rule.Verdict == RiskVerdict.Blocked);

    /// <summary>
    /// 判定一条任务的风险。文本只取用户填写的内容（标题、描述、验收标准、执行地址）；
    /// 先看禁止类，再看转人工类，最后看金额与时段两条阈值规则；都没有命中就是放行。
    /// </summary>
    public RiskAssessment Evaluate(
        string? title,
        string? description,
        IEnumerable<string>? acceptanceCriteria,
        string? executionAddress,
        decimal rewardAmount,
        DateTimeOffset deadline)
    {
        var haystack = BuildHaystack(title, description, acceptanceCriteria, executionAddress);

        foreach (var rule in Rules.Where(item => item.Verdict == RiskVerdict.Blocked))
        {
            if (rule.Matches(haystack)) return From(rule);
        }

        foreach (var rule in Rules.Where(item => item.Verdict == RiskVerdict.NeedsReview))
        {
            if (rule.Matches(haystack)) return From(rule);
        }

        if (rewardAmount > HighRewardThreshold)
        {
            return new RiskAssessment(
                RiskVerdict.NeedsReview,
                HighRewardRuleCode,
                "高金额任务",
                $"悬赏超过 {HighRewardThreshold:0} 元，需要人工确认任务内容与金额是否匹配。",
                Version);
        }

        // 截止时间按北京时间判断：深夜执行的任务风险更高（接触面、求助渠道与可核验性都更差）。
        var localDeadline = deadline.ToOffset(ChinaOffset);
        if (localDeadline.TimeOfDay >= NightWindowStart && localDeadline.TimeOfDay < NightWindowEnd)
        {
            return new RiskAssessment(
                RiskVerdict.NeedsReview,
                NightWindowRuleCode,
                "深夜时段",
                $"截止时间落在北京时间 {FormatWindow(NightWindowStart)}–{FormatWindow(NightWindowEnd)} 之间，需要人工确认可行性。",
                Version);
        }

        return RiskAssessment.Allowed(Version);
    }

    /// <summary>把目录序列化成可以落库、也可以人工读的 JSON 快照。</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new RiskRuleCatalogSnapshot(
            Version,
            HighRewardThreshold,
            FormatWindow(NightWindowStart),
            FormatWindow(NightWindowEnd),
            Rules.Select(rule => new RiskRuleSnapshot(
                rule.Code, rule.Category, rule.Verdict.ToString(), rule.Description, rule.Keywords)).ToArray()),
        JsonOptions);

    /// <summary>
    /// 从落库的快照还原目录。读不出来就抛错、绝不静默降级：
    /// 风险门禁读不到规则时，宁可让写入失败，也不能拿另一套规则去判定（那等于悄悄改了门禁的松紧）。
    /// </summary>
    public static RiskRuleCatalog FromJson(string json)
    {
        RiskRuleCatalogSnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RiskRuleCatalogSnapshot>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("风险规则目录快照不是合法 JSON，无法判定任务风险。", exception);
        }

        if (snapshot is null) throw new InvalidOperationException("风险规则目录快照为空，无法判定任务风险。");

        if (!TryParseWindow(snapshot.NightWindowStart, out var nightStart, out var startError))
            throw new InvalidOperationException($"风险规则目录快照里的深夜时段起点无法解析：{startError}");
        if (!TryParseWindow(snapshot.NightWindowEnd, out var nightEnd, out var endError))
            throw new InvalidOperationException($"风险规则目录快照里的深夜时段终点无法解析：{endError}");

        var rules = (snapshot.Rules ?? []).Select(rule =>
        {
            if (!Enum.TryParse<RiskVerdict>(rule.Verdict, ignoreCase: true, out var verdict))
                throw new InvalidOperationException($"风险规则目录快照里的结论 {rule.Verdict} 无法解析。");

            return new RiskRule(rule.Code, rule.Category, verdict, rule.Description, rule.Keywords ?? []);
        }).ToArray();

        return new RiskRuleCatalog(snapshot.Version, rules, snapshot.HighRewardThreshold, nightStart, nightEnd);
    }

    /// <summary>时段在目录里以 <c>HH:mm</c>（北京时间）表示，便于运营编辑与人工核对。</summary>
    public static string FormatWindow(TimeSpan value)
    {
        var minutes = (int)value.TotalMinutes;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    /// <summary>解析 <c>HH:mm</c> 形式的时段。允许 24:00 表示一天结束。</summary>
    public static bool TryParseWindow(string? text, out TimeSpan value, out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "时段不能为空，格式为 HH:mm。";
            return false;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hours)
            || !int.TryParse(parts[1], out var minutes)
            || hours is < 0 or > 24
            || minutes is < 0 or > 59
            || (hours == 24 && minutes != 0))
        {
            error = $"时段 {text} 不是合法的 HH:mm（小时 0–24、分钟 0–59，24 点只能是 24:00）。";
            return false;
        }

        value = TimeSpan.FromMinutes((hours * 60) + minutes);
        return true;
    }

    /// <summary>目录里所有规则的原因代码与匹配词个数，供“改了什么”的摘要与后台列表使用。</summary>
    public IReadOnlyDictionary<string, RiskRule> RulesByCode() =>
        Rules.ToDictionary(rule => rule.Code, StringComparer.Ordinal);

    private RiskAssessment From(RiskRule rule) =>
        new(rule.Verdict, rule.Code, rule.Category, rule.Description, Version);

    private static void EnsureRules(RiskRule[] rules)
    {
        if (rules.Length == 0) throw new DomainException("风险规则目录至少要有一条规则。");
        if (rules.Length > MaxRuleCount) throw new DomainException($"风险规则最多 {MaxRuleCount} 条。");

        var duplicated = rules
            .GroupBy(rule => rule.Code, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicated is not null) throw new DomainException($"风险规则的原因代码 {duplicated.Key} 重复了。");

        // 禁止类别是平台的硬门禁：一次误操作删光就不能再补，所以目录里必须始终留着至少一条。
        if (!rules.Any(rule => rule.Verdict == RiskVerdict.Blocked))
        {
            throw new DomainException("风险规则目录至少要保留一条禁止类规则：禁止类别是平台的硬门禁，不能全部删除。");
        }

        foreach (var rule in rules)
        {
            if (rule.Verdict == RiskVerdict.Allowed)
            {
                throw new DomainException($"风险规则 {rule.Code} 的结论不能是“放行”：规则只做禁止或转人工。");
            }

            if (rule.Code == HighRewardRuleCode || rule.Code == NightWindowRuleCode)
            {
                throw new DomainException($"原因代码 {rule.Code} 是阈值规则的保留代码，不能用作普通规则。");
            }

            if (rule.Keywords.Count > MaxKeywordCount)
            {
                throw new DomainException($"风险规则 {rule.Code} 的匹配词最多 {MaxKeywordCount} 个。");
            }

            // 单字匹配词会把“代取”“代送”这类正常任务一起误伤，这条底线从内置目录起就一直守着。
            var tooShort = rule.Keywords.FirstOrDefault(keyword => keyword.Length < 2);
            if (tooShort is not null)
            {
                throw new DomainException($"风险规则 {rule.Code} 的匹配词“{tooShort}”太短：匹配词至少 2 个字。");
            }

            var tooLong = rule.Keywords.FirstOrDefault(keyword => keyword.Length > MaxKeywordLength);
            if (tooLong is not null)
            {
                throw new DomainException($"风险规则 {rule.Code} 的匹配词“{tooLong}”超过 {MaxKeywordLength} 个字符。");
            }

            if (rule.Category.Length > MaxCategoryLength) throw new DomainException($"风险规则 {rule.Code} 的类别名不能超过 {MaxCategoryLength} 个字符。");
            if (rule.Description.Length > MaxDescriptionLength) throw new DomainException($"风险规则 {rule.Code} 的说明不能超过 {MaxDescriptionLength} 个字符。");
            if (rule.Code.Length > MaxCodeLength) throw new DomainException($"风险规则的原因代码不能超过 {MaxCodeLength} 个字符。");
        }
    }

    private static void EnsureThresholds(decimal highRewardThreshold, TimeSpan nightWindowStart, TimeSpan nightWindowEnd)
    {
        if (highRewardThreshold <= 0) throw new DomainException("高金额阈值必须大于 0。");
        if (highRewardThreshold > 1_000_000m) throw new DomainException("高金额阈值不能超过 1000000 元。");
        if (nightWindowStart < TimeSpan.Zero || nightWindowStart >= TimeSpan.FromDays(1)) throw new DomainException("深夜时段起点必须在 00:00 到 24:00 之间。");
        if (nightWindowEnd <= TimeSpan.Zero || nightWindowEnd > TimeSpan.FromDays(1)) throw new DomainException("深夜时段终点必须在 00:00 到 24:00 之间。");
        if (nightWindowEnd <= nightWindowStart) throw new DomainException("深夜时段终点必须晚于起点。");
    }

    private static string BuildHaystack(string? title, string? description, IEnumerable<string>? acceptanceCriteria, string? executionAddress)
    {
        var text = string.Join('\n',
            new[] { title, description, executionAddress }
                .Concat(acceptanceCriteria ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item)));

        // 去掉空白字符再匹配：否则把词用空格或换行拆开（“代 考”）就能绕开词表。
        return string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    }

    /// <summary>
    /// 内置目录（版本 1）：禁止类 6 条 + 转人工类 4 条，外加高金额与深夜时段两条阈值规则。
    /// 这份清单是平台的硬底线，运营在后台改出来的目录是它的“覆盖版”，改不回内置版本之外的任何东西。
    /// </summary>
    private static RiskRule[] BuiltInRules { get; } =
    [
        new RiskRule(
            "prohibited.exam_impersonation",
            "代考与冒名顶替",
            RiskVerdict.Blocked,
            "任务涉及代替他人考试、上课、签到或伪造本人签署，属于平台禁止的冒名行为。",
            ["代考", "替考", "代做试卷", "代写论文", "代写作业", "代上课", "代签到", "代打卡", "代人上课", "代人签到", "替人上课", "替人签到", "冒名", "顶替", "代替面试", "代签"]),

        new RiskRule(
            "prohibited.illegal_goods",
            "违禁品与危险品",
            RiskVerdict.Blocked,
            "任务涉及违禁品、管制物品或危险品，平台不得提供相关跑腿与代取送服务。",
            ["毒品", "冰毒", "大麻", "摇头丸", "枪支", "仿真枪", "弹药", "管制刀具", "炸药", "雷管", "危险化学品", "迷药", "违禁品", "走私"]),

        new RiskRule(
            "prohibited.fraud_and_identity_bypass",
            "欺诈、伪造与绕过身份核验",
            RiskVerdict.Blocked,
            "任务涉及伪造材料、破解账号或规避实名与风控核验，属于平台禁止的违法行为。",
            ["破解", "盗号", "盗取", "洗钱", "套现", "刷单", "代刷", "伪造", "假证", "假章", "绕过人脸", "绕过实名", "黑客", "木马", "撞库", "开房记录", "通话记录", "户籍", "银行流水", "征信报告", "买卖个人信息", "出售个人信息"]),

        new RiskRule(
            "prohibited.surveillance_and_harassment",
            "跟踪、偷拍与骚扰",
            RiskVerdict.Blocked,
            "任务涉及跟踪、偷拍、窃听或骚扰他人，平台不得撮合。",
            ["跟踪", "盯梢", "偷拍", "偷录", "窃听", "监听", "定位器", "追踪器", "骚扰", "恐吓", "威胁", "报复", "人肉", "曝光隐私"]),

        new RiskRule(
            "prohibited.harm_to_person",
            "危害人身安全",
            RiskVerdict.Blocked,
            "任务涉及对他人实施暴力或伤害，平台不得撮合。",
            ["打人", "殴打", "伤人", "打一顿", "揍他", "揍一顿", "教训"]),

        new RiskRule(
            "prohibited.licensed_medical",
            "必须由持证人员执行的医疗行为",
            RiskVerdict.Blocked,
            "任务涉及诊断、处方或侵入性医疗操作，必须由持证人员在合规机构内完成。",
            ["诊断", "开处方", "做手术", "输液", "打针", "拔牙"]),

        new RiskRule(
            "review.identity_documents",
            "证件与重要文件",
            RiskVerdict.NeedsReview,
            "任务涉及他人证件或重要文件的交接，需要人工确认授权与用途。",
            ["身份证", "户口本", "护照", "港澳通行证", "营业执照", "房产证", "结婚证", "学位证", "毕业证", "社保卡", "公积金", "公证书", "合同原件", "银行开户"]),

        new RiskRule(
            "review.restricted_venue",
            "受限场所",
            RiskVerdict.NeedsReview,
            "任务地点属于受限或需要准入的场所，需要人工确认是否允许第三方进入。",
            ["医院", "病房", "法院", "派出所", "公安局", "看守所", "拘留所", "监狱", "政府机关", "军事", "机场禁区", "银行金库", "幼儿园"]),

        new RiskRule(
            "review.sensitive_item",
            "敏感物品与照护对象",
            RiskVerdict.NeedsReview,
            "任务涉及现金、贵重物品、药品或需要照护的活体，需要人工确认风险与责任边界。",
            ["现金", "贵重物品", "珠宝", "名表", "黄金", "药品", "处方药", "疫苗", "血液样本", "骨灰", "遗物", "宠物"]),

        new RiskRule(
            "review.vague_item",
            "物品或用途不明",
            RiskVerdict.NeedsReview,
            "任务刻意不说明物品或用途，无法判断风险，需要人工确认。",
            ["不确定是什么", "不知道是什么", "不方便说明", "不能说明", "别问", "保密物品", "神秘包裹", "不用问"])
    ];

    /// <summary>代码内置的目录（版本 1）。数据库里还没有运营覆盖时，判定用的就是它。</summary>
    public static RiskRuleCatalog BuiltIn { get; } = new(
        BuiltInVersion, BuiltInRules, BuiltInHighRewardThreshold, BuiltInNightWindowStart, BuiltInNightWindowEnd);
}
