namespace AIToHuman.Domain.Risk;

/// <summary>
/// 一条确定性风险规则。规则不再只来自代码：内置目录（<see cref="RiskRuleCatalog.BuiltIn"/>）仍然是硬底线，
/// 运营可以在后台改出一份覆盖目录，每一版都以只追加的快照落库（见 <see cref="RiskRuleCatalogRevision"/>）。
///
/// 无论规则从哪来，<see cref="Code"/>（原因代码）都是稳定契约：
/// 它跟着判定结论一起记在任务上，所以任何一条拦截结论都能回溯到“当时用的是哪条规则、哪一版”。
/// 规则被删除或改名不会改动历史记录上的代码，历史结论按当时的目录留档。
/// </summary>
public sealed class RiskRule
{
    public RiskRule(string code, string category, RiskVerdict verdict, string description, IEnumerable<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("风险规则必须有原因代码。", nameof(code));
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("风险规则必须有类别。", nameof(category));
        if (string.IsNullOrWhiteSpace(description)) throw new ArgumentException("风险规则必须有说明。", nameof(description));

        var normalized = keywords
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0) throw new ArgumentException("风险规则至少要有一个匹配词。", nameof(keywords));

        Code = code.Trim();
        Category = category.Trim();
        Verdict = verdict;
        Description = description.Trim();
        Keywords = normalized;
    }

    /// <summary>稳定的原因代码，例如 <c>prohibited.exam_impersonation</c>。</summary>
    public string Code { get; }

    /// <summary>面向人的类别名，例如“代考与冒名顶替”。</summary>
    public string Category { get; }

    public RiskVerdict Verdict { get; }

    /// <summary>给用户看的说明：说清违反了哪一类规则，但不透露具体命中了哪个词，避免被逐字试探绕过。</summary>
    public string Description { get; }

    /// <summary>匹配用的词组。刻意避开单字（例如“代”），否则“代取”“代送”这类正常任务会被误伤。</summary>
    public IReadOnlyList<string> Keywords { get; }

    public bool Matches(string haystack) =>
        Keywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
