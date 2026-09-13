using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Contracts.Admin;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 风险规则目录的运营用例：查看当前生效的规则、翻版本历史、编辑并追加新版本、恢复到内置目录。
///
/// 四条不可让步的约定：
/// 一是**只追加**——每次编辑生成一版完整快照，旧版本永不改写，任务上记的版本号永远能还原出当时的规则；
/// 二是**必写依据**——谁改的、凭什么改都落库，并同步写一条运营审计（后台审计页能看到）；
/// 三是**乐观并发**——客户端带上它读到的版本号，与当前版本不一致就 409，避免把别人刚收紧的规则覆盖回旧的样子；
/// 四是**改坏了有退路**——恢复内置目录同样是追加一版（被恢复掉的版本仍留在历史里），
/// 而不是把表删回去，否则"谁在什么时候改坏了什么"就查不到了。
/// </summary>
public sealed class RiskRuleCatalogService(
    IRiskRuleCatalogStore store,
    IAdminAuditRepository auditRepository,
    IUserDirectory userDirectory,
    TimeProvider timeProvider,
    IUnitOfWork unitOfWork)
{
    /// <summary>编辑规则目录的审计动作。</summary>
    public const string UpdateAction = "task.risk.rules.update";

    /// <summary>恢复内置目录的审计动作。</summary>
    public const string ResetAction = "task.risk.rules.reset";

    /// <summary>审计里的对象类型。</summary>
    public const string TargetType = "risk_rule_catalog";

    /// <summary>版本历史一次最多返回多少版。</summary>
    public const int MaxVersionLimit = 100;
    public const int DefaultVersionLimit = 20;

    /// <summary>
    /// 供运营后台概述“现在按什么规则拦”：版本、阈值与规则条数。
    /// 刻意不含匹配词——这份概述也可能出现在排查记录里，词表只在明细接口按需给出。
    /// </summary>
    public RiskRuleCatalogResponse GetSummary() => DescribeSummary(store.GetLatest()?.Catalog ?? RiskRuleCatalog.BuiltIn);

    /// <summary>规则明细（含匹配词），仅运营可见。</summary>
    public RiskRuleCatalogDetailResponse GetDetail()
    {
        var latest = store.GetLatest();
        return DescribeDetail(latest?.Catalog ?? RiskRuleCatalog.BuiltIn, latest);
    }

    /// <summary>版本历史：最近的改动在前。空列表表示还没人改过，此时生效的是内置目录。</summary>
    public RiskRuleCatalogVersionListResponse ListVersions(int? limit)
    {
        var normalizedLimit = Math.Clamp(limit ?? DefaultVersionLimit, 1, MaxVersionLimit);
        var versions = store.ListVersions(normalizedLimit);
        var editors = LoadNames(versions.Select(item => item.UpdatedBy).Distinct().ToArray());

        return new(
            versions.Select(item =>
            {
                var catalog = item.Catalog;
                return new RiskRuleCatalogVersionResponse(
                    catalog.Version,
                    item.ChangeSummary,
                    item.ChangeReason,
                    item.UpdatedBy,
                    editors.GetValueOrDefault(item.UpdatedBy),
                    item.CreatedAt,
                    catalog.Rules.Count,
                    catalog.BlockedRuleCount,
                    catalog.HighRewardThreshold,
                    RiskRuleCatalog.FormatWindow(catalog.NightWindowStart),
                    RiskRuleCatalog.FormatWindow(catalog.NightWindowEnd));
            }).ToArray(),
            normalizedLimit);
    }

    /// <summary>
    /// 编辑一版规则：整份目录替换，版本号自动 +1，变更摘要与依据一起落库。
    /// 校验全部发生在领域层（版本号、至少一条禁止规则、匹配词长度、阈值与时段的合法区间），
    /// 这里只负责把请求翻译成领域对象、比版本号、写审计。
    /// </summary>
    public RiskRuleCatalogDetailResponse Update(UpdateRiskRuleCatalogRequest request, Guid actorId)
    {
        if (actorId == Guid.Empty) throw new UnauthorizedAccessException("只有运营可以修改风险规则。");

        var current = store.GetLatest()?.Catalog ?? RiskRuleCatalog.BuiltIn;
        EnsureVersionMatches(current, request.ExpectedVersion);

        var catalog = BuildCatalog(current.Version + 1, request);
        var now = timeProvider.GetUtcNow();

        // 领域层在这里拦下"什么都没改"的提交：版本号只该为真实的改动增长。
        var revision = RiskRuleCatalogRevision.Record(catalog, current, request.Reason, actorId, now);

        unitOfWork.Execute(() =>
        {
            store.Append(revision);
            auditRepository.Add(AdminAuditEntry.Record(actorId, UpdateAction, TargetType, revision.Id, AuditReason(revision), now));
        });

        return DescribeDetail(catalog, revision);
    }

    /// <summary>
    /// 恢复到代码内置目录：改坏了要有退路。
    /// 同样是**追加一版**（版本号继续往前走），而不是删掉历史——
    /// 被恢复掉的那一版仍然留在版本历史里，方便事后复盘"当时为什么要回退"。
    /// </summary>
    public RiskRuleCatalogDetailResponse ResetToBuiltIn(ResetRiskRuleCatalogRequest request, Guid actorId)
    {
        if (actorId == Guid.Empty) throw new UnauthorizedAccessException("只有运营可以修改风险规则。");

        var current = store.GetLatest()?.Catalog ?? RiskRuleCatalog.BuiltIn;
        EnsureVersionMatches(current, request.ExpectedVersion);

        if (HasSameContent(current, RiskRuleCatalog.BuiltIn))
        {
            throw new DomainException("当前生效的就是内置目录的内容，不需要恢复。");
        }

        // 版本号继续 +1，内容换回内置那一份（阈值与深夜时段也一并回退）。
        var restored = new RiskRuleCatalog(
            current.Version + 1,
            RiskRuleCatalog.BuiltIn.Rules,
            RiskRuleCatalog.BuiltIn.HighRewardThreshold,
            RiskRuleCatalog.BuiltIn.NightWindowStart,
            RiskRuleCatalog.BuiltIn.NightWindowEnd);

        var now = timeProvider.GetUtcNow();
        var summary = $"v{restored.Version}：恢复到代码内置目录（{restored.Rules.Count} 条规则、匹配词 {restored.Rules.Sum(rule => rule.Keywords.Count)} 个）。";
        var revision = RiskRuleCatalogRevision.Record(restored, current, request.Reason, actorId, now, summary);

        unitOfWork.Execute(() =>
        {
            store.Append(revision);
            auditRepository.Add(AdminAuditEntry.Record(actorId, ResetAction, TargetType, revision.Id, AuditReason(revision), now));
        });

        return DescribeDetail(restored, revision);
    }

    /// <summary>内容是否与内置目录一致（版本号不算内容）：一致就没必要"恢复"。</summary>
    private static bool HasSameContent(RiskRuleCatalog left, RiskRuleCatalog right) =>
        left.HighRewardThreshold == right.HighRewardThreshold
        && left.NightWindowStart == right.NightWindowStart
        && left.NightWindowEnd == right.NightWindowEnd
        && left.Rules.Count == right.Rules.Count
        && left.Rules.Zip(right.Rules).All(pair =>
            pair.First.Code == pair.Second.Code
            && pair.First.Category == pair.Second.Category
            && pair.First.Verdict == pair.Second.Verdict
            && pair.First.Description == pair.Second.Description
            && pair.First.Keywords.SequenceEqual(pair.Second.Keywords, StringComparer.Ordinal));

    private static RiskRuleCatalog BuildCatalog(int version, UpdateRiskRuleCatalogRequest request)
    {
        if (!RiskRuleCatalog.TryParseWindow(request.NightWindowStart, out var nightStart, out var startError))
            throw new DomainException(startError!);
        if (!RiskRuleCatalog.TryParseWindow(request.NightWindowEnd, out var nightEnd, out var endError))
            throw new DomainException(endError!);

        var rules = (request.Rules ?? [])
            .Select(rule => new RiskRule(
                Require(rule.Code, "原因代码"),
                Require(rule.Category, "类别"),
                ParseVerdict(rule.Verdict),
                Require(rule.Description, "说明"),
                rule.Keywords ?? []))
            .ToArray();

        return new RiskRuleCatalog(version, rules, request.HighRewardThreshold, nightStart, nightEnd);
    }

    private static RiskVerdict ParseVerdict(string? verdict)
    {
        if (!Enum.TryParse<RiskVerdict>(verdict?.Trim(), ignoreCase: true, out var parsed) || parsed == RiskVerdict.Allowed)
        {
            throw new DomainException($"规则结论 {verdict} 不合法：只能是 Blocked（禁止发布）或 NeedsReview（转人工）。");
        }

        return parsed;
    }

    private static string Require(string? value, string label)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) throw new DomainException($"风险规则的{label}不能为空。");
        return trimmed;
    }

    /// <summary>客户端带了版本号就先比一次：不一致说明别人刚改过，让它刷新后基于新版重新提交。</summary>
    private static void EnsureVersionMatches(RiskRuleCatalog current, int? expectedVersion)
    {
        if (expectedVersion is not { } expected || expected == current.Version) return;

        throw new ConcurrencyConflictException(
            $"风险规则已被其他人修改（当前版本 v{current.Version}，你读到的是 v{expected}），请刷新后重试。");
    }

    /// <summary>审计里的原因写“第几版 + 运营填的依据”，超出审计列宽时截断（完整依据在版本快照里）。</summary>
    private static string AuditReason(RiskRuleCatalogRevision revision)
    {
        var text = $"v{revision.Version}：{revision.ChangeReason}";
        return text.Length <= AdminAuditEntry.MaxReasonLength ? text : text[..(AdminAuditEntry.MaxReasonLength - 1)] + "…";
    }

    private IReadOnlyDictionary<Guid, string> LoadNames(IReadOnlyCollection<Guid> ids) =>
        ids.Count == 0
            ? new Dictionary<Guid, string>()
            : userDirectory.FindMany(ids).ToDictionary(item => item.Key, item => item.Value.DisplayName);

    private RiskRuleCatalogDetailResponse DescribeDetail(RiskRuleCatalog catalog, RiskRuleCatalogRevision? latest)
    {
        var editors = latest is null ? new Dictionary<Guid, string>() : LoadNames([latest.UpdatedBy]);

        return new RiskRuleCatalogDetailResponse(
            catalog.Version,
            latest is null,
            catalog.HighRewardThreshold,
            RiskRuleCatalog.FormatWindow(catalog.NightWindowStart),
            RiskRuleCatalog.FormatWindow(catalog.NightWindowEnd),
            catalog.Rules
                .Select(rule => new RiskRuleDetailResponse(rule.Code, rule.Category, rule.Verdict.ToString(), rule.Description, rule.Keywords))
                .ToArray(),
            latest?.ChangeSummary,
            latest?.ChangeReason,
            latest?.UpdatedBy,
            latest is null ? null : editors.GetValueOrDefault(latest.UpdatedBy),
            latest?.CreatedAt);
    }

    /// <summary>把一份目录映射成“概述”响应（不含匹配词）。</summary>
    public static RiskRuleCatalogResponse DescribeSummary(RiskRuleCatalog catalog) => new(
        catalog.Version,
        catalog.HighRewardThreshold,
        catalog.Rules
            .Select(rule => new RiskRuleResponse(rule.Code, rule.Category, rule.Verdict.ToString(), rule.Description, rule.Keywords.Count))
            .ToArray());
}
