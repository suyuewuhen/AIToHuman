namespace AIToHuman.Domain.Payments;

/// <summary>
/// 订单资金的托管状态。资金动作与订单状态是两件事，所以分开记：
/// 订单可能"已验收"但放款失败重试中，也可能"已取消"但退款还在路上。
/// </summary>
public enum EscrowStatus
{
    /// <summary>没有托管：运营把托管关掉了（<c>payment.provider=disabled</c>），或者是托管功能上线之前的历史订单。</summary>
    None,

    /// <summary>已托管：需求方的钱被冻结在平台侧，等验收放款或取消退款。</summary>
    Held,

    /// <summary>已全额放款给服务者。</summary>
    Released,

    /// <summary>已全额退回需求方。</summary>
    Refunded,

    /// <summary>部分放款、部分退款（争议处置后的终态）。</summary>
    Settled
}
