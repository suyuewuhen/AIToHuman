using AIToHuman.Application.Payments;

namespace AIToHuman.Infrastructure.Payments;

/// <summary>
/// 模拟支付网关：把"冻结 / 放款 / 退款"做成确定性的本地实现，用来跑通资金闭环并做回归。
///
/// 它**不持有任何金额状态**——真正的账在 <c>ledger_entries</c> 与订单的托管字段上，
/// 网关只负责"动作能不能成功"与发一个可对账的凭据号。这样做的原因：
/// 真实服务商的状态在它那边，服务端自己再存一份必然会对不上；凭据号 + 本地流水才是可对账的组合。
///
/// 测试可以通过 <see cref="FailureMode"/> 让指定动作失败，用来验证"网关不通时订单状态不变"这条口径。
/// </summary>
public sealed class SimulatedPaymentGateway : IPaymentGateway
{
    /// <summary>让哪一类动作失败（默认全部成功）。测试用它模拟网关故障。</summary>
    public SimulatedPaymentAction FailureMode { get; set; } = SimulatedPaymentAction.None;

    /// <summary>失败时返回给用户的原因。</summary>
    public string FailureReason { get; set; } = "模拟网关按配置拒绝了这个动作";

    public string Provider => "simulated";

    public PaymentGatewayResult Hold(Guid orderId, decimal amount, string currency, DateTimeOffset now) =>
        Guard(SimulatedPaymentAction.Hold, "hold", orderId, amount);

    public PaymentGatewayResult Capture(string reference, decimal amount, string currency, DateTimeOffset now) =>
        Guard(SimulatedPaymentAction.Capture, "capture", ParseOrderId(reference), amount);

    public PaymentGatewayResult Refund(string reference, decimal amount, string currency, string reason, DateTimeOffset now) =>
        Guard(SimulatedPaymentAction.Refund, "refund", ParseOrderId(reference), amount);

    private PaymentGatewayResult Guard(SimulatedPaymentAction action, string kind, Guid orderId, decimal amount)
    {
        if (amount <= 0) return PaymentGatewayResult.Failed("金额必须大于 0");
        if (action == FailureMode) return PaymentGatewayResult.Failed(FailureReason);
        // 凭据号里带订单与动作：既便于人工排查，也让"同一个动作重放"有稳定的对账键。
        return PaymentGatewayResult.Ok($"sim-{kind}-{orderId:N}");
    }

    /// <summary>从凭据号里取回订单 ID（模拟网关自己发的格式，取不到就给空——反正只用于日志与对账）。</summary>
    private static Guid ParseOrderId(string reference)
    {
        var parts = reference.Split('-');
        return parts.Length >= 3 && Guid.TryParseExact(parts[2], "N", out var id) ? id : Guid.Empty;
    }
}

/// <summary>模拟网关可以让哪一类动作失败。</summary>
public enum SimulatedPaymentAction
{
    None,
    Hold,
    Capture,
    Refund
}
