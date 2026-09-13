namespace AIToHuman.Domain.Risk;

/// <summary>
/// 确定性风险规则目录：平台禁止或需要人工审核的任务类别，全部以代码 + 版本的形式固定下来。
///
/// 为什么先做确定性规则而不是让模型判断：模型输出只能当建议，禁止类别的判定必须可复现、可回归、
/// 可解释，才能在“为什么这条任务被拦了”时有据可查（见 docs/security/security-and-risk.md 第 2、3 节）。
/// 规则只做两类动作：<see cref="RiskVerdict.Blocked"/> 一律不能发布，
/// <see cref="RiskVerdict.NeedsReview"/> 进人工队列等运营放行。
///
/// 规则改动必须同时提升 <see cref="Version"/>，否则历史任务上的判定无法区分是哪一版规则给出的。
/// </summary>
public static class RiskRuleCatalog
{
    /// <summary>规则目录版本。改规则（增删词、调整阈值、调整结论）时必须 +1。</summary>
    public const int Version = 1;

    /// <summary>高金额阈值（元）：超过它转人工，避免大额任务直接进大厅。</summary>
    public const decimal HighRewardThreshold = 5000m;

    /// <summary>深夜时段按北京时间计算，区间为 [00:00, 06:00)。</summary>
    public static readonly TimeSpan NightWindowStart = TimeSpan.Zero;

    public static readonly TimeSpan NightWindowEnd = TimeSpan.FromHours(6);

    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    /// <summary>禁止发布（Blocked）。命中任意一条就一律不能发布，人工也不能放行。</summary>
    public static IReadOnlyList<RiskRule> Rules { get; } =
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

    /// <summary>
    /// 判定一条任务的风险。文本只取用户填写的内容（标题、描述、验收标准、执行地址）；
    /// 先看禁止类，再看转人工类，最后看金额与时段两条阈值规则；都没有命中就是放行。
    /// </summary>
    public static RiskAssessment Evaluate(
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
                "review.high_reward",
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
                "review.night_window",
                "深夜时段",
                $"截止时间落在北京时间 {NightWindowStart:hh\\:mm}–{NightWindowEnd:hh\\:mm} 之间，需要人工确认可行性。",
                Version);
        }

        return RiskAssessment.Allowed(Version);
    }

    private static RiskAssessment From(RiskRule rule) =>
        new(rule.Verdict, rule.Code, rule.Category, rule.Description, Version);

    private static string BuildHaystack(string? title, string? description, IEnumerable<string>? acceptanceCriteria, string? executionAddress)
    {
        var text = string.Join('\n',
            new[] { title, description, executionAddress }
                .Concat(acceptanceCriteria ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item)));

        // 去掉空白字符再匹配：否则把词用空格或换行拆开（“代 考”）就能绕开词表。
        return string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    }
}
