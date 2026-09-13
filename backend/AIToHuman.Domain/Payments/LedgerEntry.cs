using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Domain.Payments;

/// <summary>资金流水的账户。托管模式下每一笔都是"从一个账户转到另一个账户"，没有凭空出现的钱。</summary>
public enum LedgerAccount
{
    /// <summary>需求方（付款方）。</summary>
    OwnerFunds,

    /// <summary>平台托管：钱在平台侧冻结。</summary>
    Escrow,

    /// <summary>服务者应得。</summary>
    WorkerPayout
}

/// <summary>这一笔流水是什么动作产生的。</summary>
public enum LedgerEntryKind
{
    /// <summary>下单时冻结需求方资金（OwnerFunds → Escrow）。</summary>
    Hold,

    /// <summary>验收通过全额放款（Escrow → WorkerPayout）。</summary>
    Release,

    /// <summary>订单取消全额退款（Escrow → OwnerFunds）。</summary>
    Refund,

    /// <summary>争议处置：部分放款给服务者（Escrow → WorkerPayout）。</summary>
    PartialRelease,

    /// <summary>争议处置：部分退给需求方（Escrow → OwnerFunds）。</summary>
    PartialRefund
}

/// <summary>
/// 资金流水：**只追加**，一行就是一次账户间转账（借/贷两个账户 + 金额 + 动作 + 时间）。
///
/// 为什么用复式记账而不是只记一个"订单已放款"的标记：
/// 一是钱的每一步都能对上账（同一订单的流水加总能还原冻结、放款、退款各多少）；
/// 二是争议里的部分赔付（一半给服务者、一半退需求方）天然表达成两笔转账，不需要额外的字段去拼；
/// 三是审计要的是"谁在什么时候动了多少钱"，而这正是这张表的最小单位。
///
/// 本轮不抽佣金：平台只做托管与转付，<see cref="LedgerAccount"/> 里没有平台收入账户——
/// 真有佣金时应当新增账户与对应的分账流水，而不是把差额留在托管账户里。
/// </summary>
public sealed class LedgerEntry
{
    /// <summary>流水备注的长度上限。</summary>
    public const int MaxNoteLength = 200;

    private LedgerEntry(
        Guid id,
        Guid orderId,
        Guid taskId,
        LedgerAccount debitAccount,
        LedgerAccount creditAccount,
        decimal amount,
        string currency,
        LedgerEntryKind kind,
        string? note,
        DateTimeOffset occurredAt)
    {
        Id = id;
        OrderId = orderId;
        TaskId = taskId;
        DebitAccount = debitAccount;
        CreditAccount = creditAccount;
        Amount = amount;
        Currency = currency;
        Kind = kind;
        Note = note;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    public Guid OrderId { get; }

    public Guid TaskId { get; }

    /// <summary>转出账户。</summary>
    public LedgerAccount DebitAccount { get; }

    /// <summary>转入账户。</summary>
    public LedgerAccount CreditAccount { get; }

    /// <summary>金额（正数；方向由借/贷账户表达）。</summary>
    public decimal Amount { get; }

    public string Currency { get; }

    public LedgerEntryKind Kind { get; }

    /// <summary>给双方与运营看的说明（例如"验收通过，放款给服务者"）。</summary>
    public string? Note { get; }

    public DateTimeOffset OccurredAt { get; }

    /// <summary>记一笔转账。金额必须为正、币种必须一致、借貸账户不能相同——这三条是账本能对上账的前提。</summary>
    public static LedgerEntry Transfer(
        Order order,
        LedgerAccount debitAccount,
        LedgerAccount creditAccount,
        decimal amount,
        LedgerEntryKind kind,
        string? note,
        DateTimeOffset now)
    {
        return Record(order.Id, order.TaskId, debitAccount, creditAccount, amount, order.Reward.Currency, kind, note, now);
    }

    public static LedgerEntry Record(
        Guid orderId,
        Guid taskId,
        LedgerAccount debitAccount,
        LedgerAccount creditAccount,
        decimal amount,
        string currency,
        LedgerEntryKind kind,
        string? note,
        DateTimeOffset now)
    {
        if (orderId == Guid.Empty) throw new DomainException("资金流水必须挂在订单上。");
        if (amount <= 0) throw new DomainException("资金流水的金额必须大于 0。");
        if (debitAccount == creditAccount) throw new DomainException("资金流水的转出与转入账户不能相同。");
        if (string.IsNullOrWhiteSpace(currency)) throw new DomainException("资金流水必须有币种。");

        var trimmedNote = note?.Trim();
        if (trimmedNote is { Length: > MaxNoteLength }) trimmedNote = trimmedNote[..MaxNoteLength];

        return new LedgerEntry(Guid.NewGuid(), orderId, taskId, debitAccount, creditAccount, amount, currency.Trim().ToUpperInvariant(), kind, string.IsNullOrEmpty(trimmedNote) ? null : trimmedNote, UtcTimestamp.Normalize(now));
    }

    public static LedgerEntry Rehydrate(
        Guid id,
        Guid orderId,
        Guid taskId,
        LedgerAccount debitAccount,
        LedgerAccount creditAccount,
        decimal amount,
        string currency,
        LedgerEntryKind kind,
        string? note,
        DateTimeOffset occurredAt) =>
        new(id, orderId, taskId, debitAccount, creditAccount, amount, currency, kind, note, UtcTimestamp.Normalize(occurredAt));
}
