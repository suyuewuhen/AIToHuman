namespace AIToHuman.Domain.Risk;

/// <summary>
/// 一条确定性风险规则。规则是代码里的固定目录（<see cref="RiskRuleCatalog"/>），不是可以随手改的数据：
/// 规则一旦上线就要有稳定的 <see cref="Code"/>（原因代码）与 <see cref="Version"/>，
/// 这样任何一条拦截结论都能回溯到“当时用的是哪条规则、哪一版”。
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
