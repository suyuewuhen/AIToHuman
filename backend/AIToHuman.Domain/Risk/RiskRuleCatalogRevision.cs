using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Risk;

/// <summary>
/// 一版风险规则目录的历史记录：只追加，不更新也不删除。
///
/// 为什么存"版本快照 + 谁改的 + 凭什么改"而不是只存当前值：
/// 风险门禁的松紧就是平台的合规姿态，被问起"上个月为什么这条任务被拦"或"这条规则是谁让加的"时，
/// 必须能给出当时的完整目录与理由，而不是只能看到现在的样子。
/// 任务上记的是判定时的 <see cref="RiskRuleCatalog.Version"/>，配合这张表就能完整复现当时的判定。
/// </summary>
public sealed class RiskRuleCatalogRevision
{
    /// <summary>变更依据的长度上限（运营必填）。</summary>
    public const int MaxReasonLength = 200;

    /// <summary>变化摘要的长度上限，避免后台一次改很多条时把摘要写成一篇文。</summary>
    public const int MaxSummaryLength = 400;

    private RiskRuleCatalogRevision(
        Guid id,
        string catalogJson,
        string changeSummary,
        string changeReason,
        Guid updatedBy,
        DateTimeOffset createdAt)
    {
        Id = id;
        CatalogJson = catalogJson;
        ChangeSummary = changeSummary;
        ChangeReason = changeReason;
        UpdatedBy = updatedBy;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    /// <summary>这一版的完整目录快照（JSON）。</summary>
    public string CatalogJson { get; }

    /// <summary>人可读的变化摘要，例如“新增匹配词 3 个；删除规则 1 条（review.vague_item）”。</summary>
    public string ChangeSummary { get; }

    /// <summary>运营填写的变更依据，必填。</summary>
    public string ChangeReason { get; }

    public Guid UpdatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>从快照还原出来的目录；解析不了会抛错，绝不静默降级。</summary>
    public RiskRuleCatalog Catalog => RiskRuleCatalog.FromJson(CatalogJson);

    /// <summary>这一版对应的版本号（与任务上记的 <c>RiskRuleVersion</c> 同一套编号）。</summary>
    public int Version => Catalog.Version;

    /// <summary>
    /// 记一版新目录。<paramref name="previous"/> 是这次编辑前的生效目录（通常就是上一版，没有覆盖时是内置目录），
    /// 版本号必须正好 +1，并且内容必须真的变了——否则历史里会堆出一串"什么都没改"的版本，反而看不清哪次改动生效在哪。
    /// <paramref name="summary"/> 用来覆盖自动算出来的变化摘要（用于"恢复内置目录"这类语义明确的动作）；
    /// 调用方给摘要时要自己保证这次改动确实有内容变化。
    /// </summary>
    public static RiskRuleCatalogRevision Record(
        RiskRuleCatalog catalog,
        RiskRuleCatalog? previous,
        string reason,
        Guid actorId,
        DateTimeOffset createdAt,
        string? summary = null)
    {
        // 依据先校验：这样"没写依据"和"什么都没改"同时成立时，报出的是运营一眼能改的那条（缺依据）。
        var normalizedReason = EnsureReason(reason);
        if (actorId == Guid.Empty) throw new DomainException("风险规则变更必须记录操作人。");
        if (previous is not null && catalog.Version != previous.Version + 1)
        {
            throw new DomainException($"风险规则版本号必须比上一版（v{previous.Version}）大 1，收到 v{catalog.Version}。");
        }

        var description = string.IsNullOrWhiteSpace(summary)
            ? Summarize(previous, catalog) ?? throw new DomainException("风险规则内容没有变化，无需更新。")
            : summary.Trim();

        return new RiskRuleCatalogRevision(
            Guid.NewGuid(),
            catalog.ToJson(),
            Truncate(description, MaxSummaryLength),
            normalizedReason,
            actorId,
            UtcTimestamp.Normalize(createdAt));
    }

    public static RiskRuleCatalogRevision Rehydrate(
        Guid id,
        string catalogJson,
        string changeSummary,
        string changeReason,
        Guid updatedBy,
        DateTimeOffset createdAt) =>
        new(id, catalogJson, changeSummary, changeReason, updatedBy, UtcTimestamp.Normalize(createdAt));

    /// <summary>
    /// 描述这一版相对上一版改了什么。返回 <c>null</c> 表示内容完全一致（调用方据此拒绝空改动）。
    /// 摘要只讲"改了几条、动了哪些代码"，不把匹配词本身写进来：摘要会出现在运营审计里，
    /// 而词表本身只在规则明细接口里按需展示。
    /// </summary>
    public static string? Summarize(RiskRuleCatalog? previous, RiskRuleCatalog current)
    {
        if (previous is null)
        {
            return $"建立运营目录 v{current.Version}：{current.Rules.Count} 条规则、匹配词 {current.Rules.Sum(rule => rule.Keywords.Count)} 个。";
        }

        var before = previous.RulesByCode();
        var after = current.RulesByCode();
        var parts = new List<string>();

        var added = after.Keys.Where(code => !before.ContainsKey(code)).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        if (added.Length > 0) parts.Add($"新增规则 {added.Length} 条（{ListCodes(added)}）");

        var removed = before.Keys.Where(code => !after.ContainsKey(code)).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        if (removed.Length > 0) parts.Add($"删除规则 {removed.Length} 条（{ListCodes(removed)}）");

        var verdictChanged = after
            .Where(item => before.TryGetValue(item.Key, out var old) && old.Verdict != item.Value.Verdict)
            .Select(item => $"{item.Key} {before[item.Key].Verdict} → {item.Value.Verdict}")
            .ToArray();
        if (verdictChanged.Length > 0) parts.Add($"结论调整 {verdictChanged.Length} 条（{ListCodes(verdictChanged)}）");

        var addedKeywords = 0;
        var removedKeywords = 0;
        var rewritten = 0;
        foreach (var (code, rule) in after)
        {
            if (!before.TryGetValue(code, out var old)) continue;

            addedKeywords += rule.Keywords.Except(old.Keywords, StringComparer.Ordinal).Count();
            removedKeywords += old.Keywords.Except(rule.Keywords, StringComparer.Ordinal).Count();
            if (rule.Category != old.Category || rule.Description != old.Description) rewritten++;
        }

        if (addedKeywords > 0) parts.Add($"新增匹配词 {addedKeywords} 个");
        if (removedKeywords > 0) parts.Add($"删除匹配词 {removedKeywords} 个");
        if (rewritten > 0) parts.Add($"改写类别或说明 {rewritten} 条");

        if (previous.HighRewardThreshold != current.HighRewardThreshold)
        {
            parts.Add($"高金额阈值 {previous.HighRewardThreshold:0.##} → {current.HighRewardThreshold:0.##} 元");
        }

        if (previous.NightWindowStart != current.NightWindowStart || previous.NightWindowEnd != current.NightWindowEnd)
        {
            parts.Add($"深夜时段 {RiskRuleCatalog.FormatWindow(previous.NightWindowStart)}–{RiskRuleCatalog.FormatWindow(previous.NightWindowEnd)}"
                + $" → {RiskRuleCatalog.FormatWindow(current.NightWindowStart)}–{RiskRuleCatalog.FormatWindow(current.NightWindowEnd)}");
        }

        return parts.Count == 0 ? null : $"v{current.Version}：{string.Join("；", parts)}。";
    }

    private static string ListCodes(IReadOnlyCollection<string> codes)
    {
        const int shown = 3;
        var text = string.Join("、", codes.Take(shown));
        return codes.Count <= shown ? text : $"{text} 等 {codes.Count} 条";
    }

    private static string EnsureReason(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new DomainException("修改风险规则必须写明依据。");
        if (trimmed.Length > MaxReasonLength) throw new DomainException($"修改风险规则的依据不能超过 {MaxReasonLength} 个字符。");
        return trimmed;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
