namespace AIToHuman.Domain.Risk;

/// <summary>
/// 发布后复检的结论，用来告诉用例层"还需要做哪些带副作用的事"（冻结订单、通知、审计）。
/// 领域层只判定与改自己的状态，碰订单和通知是应用层的事。
/// </summary>
public enum RiskEnforcementOutcome
{
    /// <summary>复检后结论没变（规则版本号可能被刷新），落库即可。</summary>
    Unchanged,

    /// <summary>变成"需人工复核"：任务保持在线并进运营复检队列，通知所有者。</summary>
    FlaggedForRecheck,

    /// <summary>变成"禁止发布"且没有订单：任务已自动下架，通知所有者与报名中的服务者。</summary>
    Unpublished,

    /// <summary>变成"禁止发布"但已经有订单：应用层要冻结订单（平台发起争议），并通知双方。</summary>
    OrderFrozen
}
