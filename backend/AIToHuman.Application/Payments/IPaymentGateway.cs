using AIToHuman.Domain.Payments;

namespace AIToHuman.Application.Payments;

/// <summary>
/// 资金动作的结果。失败时带一句人话的原因——它会直接出现在用户的错误提示里，
/// 所以不要写成网关的内部错误码。
/// </summary>
public sealed record PaymentGatewayResult(bool Succeeded, string? Reference, string? FailureReason)
{
    public static PaymentGatewayResult Ok(string reference) => new(true, reference, null);

    public static PaymentGatewayResult Failed(string reason) => new(false, null, reason);
}

/// <summary>
/// 支付/托管网关的端口。
///
/// 为什么先做抽象 + 模拟网关：资金逻辑（冻结、放款、退款、分账与账本）是可以现在就做对、
/// 也能完整回归的部分；接哪家支付服务商是后一步的配置问题，不应该卡住整个闭环。
/// 真实服务商要满足的两条契约（写在这里，免得以后接的时候忘掉）：
/// 一是同一个订单的同一个动作要能用**幂等键**重放（本端口用订单 ID + 动作派生）；
/// 二是网关侧要有可对账的流水号，服务端把它记在订单与账本上。
/// </summary>
public interface IPaymentGateway
{
    /// <summary>网关标识（写进凭据与日志，便于排查"这笔钱走的是哪条通道"）。</summary>
    string Provider { get; }

    /// <summary>冻结需求方资金（下单时）。</summary>
    PaymentGatewayResult Hold(Guid orderId, decimal amount, string currency, DateTimeOffset now);

    /// <summary>把冻结的资金放款给服务者。</summary>
    PaymentGatewayResult Capture(string reference, decimal amount, string currency, DateTimeOffset now);

    /// <summary>把冻结的资金退回需求方。</summary>
    PaymentGatewayResult Refund(string reference, decimal amount, string currency, string reason, DateTimeOffset now);
}

/// <summary>资金流水的存储：只追加，不更新也不删除。</summary>
public interface ILedgerRepository
{
    void Add(LedgerEntry entry);

    /// <summary>某笔订单的全部流水，按发生时间升序（对账时按时间读最直观）。</summary>
    IReadOnlyCollection<LedgerEntry> ListByOrder(Guid orderId);

    /// <summary>最近的流水（运营排查用），按时间倒序。</summary>
    IReadOnlyCollection<LedgerEntry> ListRecent(int limit);
}
