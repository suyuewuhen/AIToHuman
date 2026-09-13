using AIToHuman.Domain.Risk;

namespace AIToHuman.Application.Risk;

/// <summary>
/// 风险规则目录的持久化。
///
/// 目录是"只追加的版本快照"：写入只做追加，读取始终取版本号最大的那一版。
/// 数据库里一条都没有时，生效目录是代码里的内置目录（<see cref="RiskRuleCatalog.BuiltIn"/>，版本 1）——
/// 所以任何一次运营编辑都是版本 2 起步，历史里不会出现"从未存在过的第 1 版"。
/// </summary>
public interface IRiskRuleCatalogStore
{
    /// <summary>取版本号最大的一版；还没有任何运营编辑时返回 null。</summary>
    RiskRuleCatalogRevision? GetLatest();

    /// <summary>按版本号倒序返回最近的若干版。</summary>
    IReadOnlyCollection<RiskRuleCatalogRevision> ListVersions(int limit);

    /// <summary>追加一版（只追加，不更新也不删除）。</summary>
    void Append(RiskRuleCatalogRevision revision);
}

/// <summary>
/// 生效目录的提供者：把"取哪一版"这件事收在一处，任务写入路径（<c>TaskService</c>）只依赖这个接口。
/// 配套的用例服务用同一个 <see cref="IRiskRuleCatalogStore"/> 写新版本，因此写完立刻对后续判定生效。
/// </summary>
public interface IRiskRuleCatalogProvider
{
    RiskRuleCatalog GetEffective();
}

/// <summary>默认实现：直接读存储，没有运营覆盖时回退到内置目录。</summary>
public sealed class RiskRuleCatalogStoreProvider(IRiskRuleCatalogStore store) : IRiskRuleCatalogProvider
{
    public RiskRuleCatalog GetEffective() => store.GetLatest()?.Catalog ?? RiskRuleCatalog.BuiltIn;
}
