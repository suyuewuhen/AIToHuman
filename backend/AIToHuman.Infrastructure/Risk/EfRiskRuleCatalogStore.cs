using AIToHuman.Application.Risk;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Risk;

/// <summary>
/// 风险规则目录的 EF 实现：只插入与查询。
/// 读取始终按版本号倒序取最新一版——版本号由应用层在追加前算好（当前 +1），
/// 因此"哪一版生效"永远只有一个答案，不需要额外的"是否生效"标志位。
/// </summary>
public sealed class EfRiskRuleCatalogStore(TaskDbContext db) : IRiskRuleCatalogStore
{
    public RiskRuleCatalogRevision? GetLatest()
    {
        var record = db.RiskRuleCatalogRevisions
            .AsNoTracking()
            .OrderByDescending(item => item.Version)
            .FirstOrDefault();

        return record is null ? null : Map(record);
    }

    public IReadOnlyCollection<RiskRuleCatalogRevision> ListVersions(int limit) => db.RiskRuleCatalogRevisions
        .AsNoTracking()
        .OrderByDescending(item => item.Version)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public void Append(RiskRuleCatalogRevision revision)
    {
        db.RiskRuleCatalogRevisions.Add(new RiskRuleCatalogRevisionRecord
        {
            Id = revision.Id,
            // 版本号以快照内容为准（RiskRuleCatalogRevision.Version 就是解析快照得到的），避免两处各写一遍版本号。
            Version = revision.Version,
            CatalogJson = revision.CatalogJson,
            ChangeSummary = revision.ChangeSummary,
            ChangeReason = revision.ChangeReason,
            UpdatedBy = revision.UpdatedBy,
            CreatedAt = revision.CreatedAt
        });

        db.SaveChanges();
    }

    private static RiskRuleCatalogRevision Map(RiskRuleCatalogRevisionRecord record) => RiskRuleCatalogRevision.Rehydrate(
        record.Id, record.CatalogJson, record.ChangeSummary, record.ChangeReason, record.UpdatedBy, record.CreatedAt);
}
