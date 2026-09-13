using AIToHuman.Application.Risk;
using AIToHuman.Domain.Risk;

namespace AIToHuman.Infrastructure.Risk;

/// <summary>
/// 风险规则目录的内存实现（无 PostgreSQL 时使用，也用在同一进程的测试里）。
/// 与 EF 实现语义一致：只追加、按版本倒序取最新一版。
/// </summary>
public sealed class InMemoryRiskRuleCatalogStore : IRiskRuleCatalogStore
{
    private readonly List<RiskRuleCatalogRevision> revisions = [];
    private readonly Lock gate = new();

    public RiskRuleCatalogRevision? GetLatest()
    {
        lock (gate)
        {
            return revisions.Count == 0 ? null : revisions.MaxBy(item => item.Version);
        }
    }

    public IReadOnlyCollection<RiskRuleCatalogRevision> ListVersions(int limit)
    {
        lock (gate)
        {
            return revisions
                .OrderByDescending(item => item.Version)
                .Take(limit)
                .ToArray();
        }
    }

    public void Append(RiskRuleCatalogRevision revision)
    {
        lock (gate)
        {
            if (revisions.Any(item => item.Version == revision.Version))
            {
                throw new InvalidOperationException($"风险规则版本 {revision.Version} 已经存在。");
            }

            revisions.Add(revision);
        }
    }
}
