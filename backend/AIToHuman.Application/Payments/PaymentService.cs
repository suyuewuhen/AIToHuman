using AIToHuman.Application.Common;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Payments;

namespace AIToHuman.Application.Payments;

/// <summary>
/// 订单资金的用例：下单冻结、验收放款、取消退款、争议分账，每一步都同时改订单的托管状态并写一条流水。
///
/// 三条口径：
/// 一是**钱与状态一起动**：资金动作与订单保存落在同一个工作单元里（调用方保证），
///    不允许出现"订单已完成但没放款"或"已取消但没退款"的中间态；
/// 二是**失败就不改状态**（fail closed）：网关不通时抛 <see cref="DomainException"/>，
///    订单状态与流水都不变，用户可以稍后重试——宁可挡住一次验收，也不留一笔说不清的钱；
/// 三是**可以整体关掉**（<c>payment.provider=disabled</c>）：托管关掉时订单不带托管信息、也不写流水，
///    行为与托管功能上线之前完全一致（便于本地联调，也是网关出问题时的降级开关）。
///
/// 已知缺口（留给后续）：失败重试队列与自动对账还没有——现在依赖调用方重试；
/// 平台佣金也没有抽，账本里没有平台收入账户（真有佣金时应新增账户与分账流水，而不是把差额留在托管账户）。
/// </summary>
public sealed class PaymentService(
    IPaymentGateway gateway,
    ISettingsProvider settings,
    ILedgerRepository ledger,
    TimeProvider timeProvider,
    IUnitOfWork unitOfWork)
{
    /// <summary>托管是否启用。默认启用；关掉之后订单不带托管信息、也不写流水。</summary>
    public bool EscrowEnabled => settings.GetChoice(SettingKeys.PaymentProvider, SettingKeys.PaymentProviderSimulated) != SettingKeys.PaymentProviderDisabled;

    /// <summary>
    /// 下单时冻结需求方资金。失败直接抛错——**没有托管成功就不该把任务分配出去**：
    /// 选人这一步同时改任务、建订单、冻结资金，三者要么都成立、要么都不成立（同一个事务）。
    /// </summary>
    public void HoldFor(Order order, Guid taskId)
    {
        if (!EscrowEnabled) return;
        if (order.EscrowAwaitingSettlement) throw new DomainException("这笔订单的资金已经冻结过了。");

        var now = timeProvider.GetUtcNow();
        var result = gateway.Hold(order.Id, order.Reward.Amount, order.Reward.Currency, now);
        if (!result.Succeeded || result.Reference is null)
        {
            throw new DomainException($"资金托管失败，任务没有分配出去：{result.FailureReason ?? "支付服务暂时不可用"}。请稍后重试。");
        }

        order.HoldEscrow(result.Reference, now);
        unitOfWork.Execute(() => ledger.Add(LedgerEntry.Transfer(
            order, LedgerAccount.OwnerFunds, LedgerAccount.Escrow, order.Reward.Amount, LedgerEntryKind.Hold,
            "下单托管需求方资金", now)));
    }

    /// <summary>验收通过：托管全额放款给服务者。</summary>
    public void ReleaseFor(Order order)
    {
        if (!EscrowEnabled || order.EscrowStatus == EscrowStatus.None) return;

        var now = timeProvider.GetUtcNow();
        var amount = order.EscrowAmount;
        var result = gateway.Capture(order.PaymentReference ?? string.Empty, amount, order.Reward.Currency, now);
        if (!result.Succeeded)
        {
            throw new DomainException($"放款失败，订单没有完成：{result.FailureReason ?? "支付服务暂时不可用"}。请稍后重试。");
        }

        order.ReleaseEscrow(now);
        unitOfWork.Execute(() => ledger.Add(LedgerEntry.Transfer(
            order, LedgerAccount.Escrow, LedgerAccount.WorkerPayout, amount, LedgerEntryKind.Release,
            "验收通过，托管资金放款给服务者", now)));
    }

    /// <summary>订单取消：托管全额退回需求方。</summary>
    public void RefundFor(Order order, string? reason)
    {
        if (!EscrowEnabled || order.EscrowStatus == EscrowStatus.None) return;

        var now = timeProvider.GetUtcNow();
        var amount = order.EscrowAmount;
        var result = gateway.Refund(order.PaymentReference ?? string.Empty, amount, order.Reward.Currency, reason ?? "订单取消", now);
        if (!result.Succeeded)
        {
            throw new DomainException($"退款失败，订单没有取消：{result.FailureReason ?? "支付服务暂时不可用"}。请稍后重试。");
        }

        order.RefundEscrow(now);
        unitOfWork.Execute(() => ledger.Add(LedgerEntry.Transfer(
            order, LedgerAccount.Escrow, LedgerAccount.OwnerFunds, amount, LedgerEntryKind.Refund,
            "订单取消，托管资金退回需求方", now)));
    }

    /// <summary>
    /// 争议处置后的分账：<paramref name="workerAmount"/> 给服务者、其余退给需求方。
    /// 两笔转账分别入账（部分放款 + 部分退款），因此"一半赔付"在流水里是看得见的，
    /// 而不是被压缩成一个"已处置"的标记。
    /// </summary>
    public void SettleFor(Order order, decimal workerAmount, string note)
    {
        if (!EscrowEnabled || order.EscrowStatus == EscrowStatus.None) return;

        var now = timeProvider.GetUtcNow();
        var ownerAmount = order.EscrowAmount - workerAmount;
        if (workerAmount < 0 || ownerAmount < 0)
        {
            throw new DomainException($"赔付金额必须在 0 到托管金额（{order.EscrowAmount:0.##}）之间。");
        }

        if (workerAmount > 0)
        {
            var capture = gateway.Capture(order.PaymentReference ?? string.Empty, workerAmount, order.Reward.Currency, now);
            if (!capture.Succeeded)
            {
                throw new DomainException($"赔付放款失败，订单与资金都没有变化：{capture.FailureReason ?? "支付服务暂时不可用"}。请稍后重试。");
            }
        }

        if (ownerAmount > 0)
        {
            var refund = gateway.Refund(order.PaymentReference ?? string.Empty, ownerAmount, order.Reward.Currency, note, now);
            if (!refund.Succeeded)
            {
                throw new DomainException($"赔付退款失败，订单与资金都没有变化：{refund.FailureReason ?? "支付服务暂时不可用"}。请稍后重试。");
            }
        }

        order.SettleEscrow(workerAmount, ownerAmount, now);
        unitOfWork.Execute(() =>
        {
            if (workerAmount > 0)
            {
                ledger.Add(LedgerEntry.Transfer(order, LedgerAccount.Escrow, LedgerAccount.WorkerPayout, workerAmount, LedgerEntryKind.PartialRelease, note, now));
            }

            if (ownerAmount > 0)
            {
                ledger.Add(LedgerEntry.Transfer(order, LedgerAccount.Escrow, LedgerAccount.OwnerFunds, ownerAmount, LedgerEntryKind.PartialRefund, note, now));
            }
        });
    }

    /// <summary>某笔订单的资金流水：只读，双方参与者与运营都能看（"钱去哪了"不该只有平台知道）。</summary>
    public IReadOnlyCollection<LedgerEntry> ListByOrder(Guid orderId) => ledger.ListByOrder(orderId);

    /// <summary>最近的资金流水，供运营排查。</summary>
    public IReadOnlyCollection<LedgerEntry> ListRecent(int limit) => ledger.ListRecent(Math.Clamp(limit, 1, 200));
}
