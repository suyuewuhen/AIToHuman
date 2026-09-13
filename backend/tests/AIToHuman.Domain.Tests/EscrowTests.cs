using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Payments;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 订单资金托管的领域规则：冻结、放款、退款、争议分账，以及"钱不许凭空多出来或少下去"这条底线。
/// </summary>
public sealed class EscrowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Holding_escrow_freezes_the_reward_and_cannot_be_repeated()
    {
        var order = NewOrder(60);

        order.HoldEscrow("sim-hold-1", Now);

        Assert.Equal(EscrowStatus.Held, order.EscrowStatus);
        Assert.Equal(60m, order.EscrowAmount);
        Assert.Equal(60m, order.EscrowBalance);
        Assert.Equal("sim-hold-1", order.PaymentReference);
        Assert.Equal(Now, order.EscrowHeldAt);
        Assert.True(order.EscrowAwaitingSettlement);

        Assert.Throws<DomainException>(() => order.HoldEscrow("sim-hold-2", Now));
    }

    [Fact]
    public void Holding_escrow_requires_a_reference_and_a_positive_amount()
    {
        Assert.Throws<DomainException>(() => NewOrder(60).HoldEscrow("   ", Now));
        Assert.Throws<DomainException>(() => NewOrder(0).HoldEscrow("sim-hold-1", Now));
    }

    [Fact]
    public void Releasing_pays_the_worker_in_full_and_leaves_nothing_frozen()
    {
        var order = Holding(100);

        order.ReleaseEscrow(Now.AddHours(1));

        Assert.Equal(EscrowStatus.Released, order.EscrowStatus);
        Assert.Equal(100m, order.ReleasedAmount);
        Assert.Equal(0m, order.RefundedAmount);
        Assert.Equal(0m, order.EscrowBalance);
        Assert.Equal(Now.AddHours(1), order.EscrowSettledAt);
        // 结算过的托管不能再动一次。
        Assert.Throws<DomainException>(() => order.ReleaseEscrow(Now.AddHours(2)));
    }

    [Fact]
    public void Refunding_returns_everything_to_the_owner()
    {
        var order = Holding(100);

        order.RefundEscrow(Now.AddHours(1));

        Assert.Equal(EscrowStatus.Refunded, order.EscrowStatus);
        Assert.Equal(0m, order.ReleasedAmount);
        Assert.Equal(100m, order.RefundedAmount);
    }

    [Fact]
    public void A_dispute_settlement_splits_the_escrow_and_must_add_up()
    {
        var order = Holding(100);

        order.SettleEscrow(60m, 40m, Now.AddHours(1));

        Assert.Equal(EscrowStatus.Settled, order.EscrowStatus);
        Assert.Equal(60m, order.ReleasedAmount);
        Assert.Equal(40m, order.RefundedAmount);
        Assert.Equal(0m, order.EscrowBalance);
    }

    [Theory]
    // 加起来不等于托管金额：不允许有"不知道去哪了"的差额。
    [InlineData(60, 30)]
    [InlineData(70, 40)]
    // 负数与全零都要挡掉。
    [InlineData(-10, 110)]
    [InlineData(0, 0)]
    public void A_settlement_that_does_not_add_up_is_rejected(decimal workerAmount, decimal ownerAmount)
    {
        var order = Holding(100);

        Assert.Throws<DomainException>(() => order.SettleEscrow(workerAmount, ownerAmount, Now.AddHours(1)));
        Assert.Equal(EscrowStatus.Held, order.EscrowStatus);
    }

    [Fact]
    public void Escrow_cannot_be_settled_without_holding_and_only_once()
    {
        var neverHeld = NewOrder(100);
        var error = Assert.Throws<DomainException>(() => neverHeld.RefundEscrow(Now));
        Assert.Contains("没有托管资金", error.Message);

        var settled = Holding(100);
        settled.ReleaseEscrow(Now.AddHours(1));
        var again = Assert.Throws<DomainException>(() => settled.SettleEscrow(50m, 50m, Now.AddHours(2)));
        Assert.Contains("已经处理过", again.Message);
    }

    [Fact]
    public void Turning_a_full_release_or_refund_into_a_settlement_reports_the_right_status()
    {
        Assert.Equal(EscrowStatus.Released, Holding(50).Settle(50m, 0m).EscrowStatus);
        Assert.Equal(EscrowStatus.Refunded, Holding(50).Settle(0m, 50m).EscrowStatus);
        Assert.Equal(EscrowStatus.Settled, Holding(50).Settle(20m, 30m).EscrowStatus);
    }

    private static Order NewOrder(decimal reward) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "代取文件", new Money(reward), Now);

    private static Order Holding(decimal reward)
    {
        var order = NewOrder(reward);
        order.HoldEscrow("sim-hold-1", Now);
        return order;
    }
}

/// <summary>资金流水自身的规则：一行一次账户间转账，金额为正、借贷两端不同、必须挂在订单上。</summary>
public sealed class LedgerEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_transfer_records_both_accounts_the_amount_and_the_note()
    {
        var order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "代取文件", new Money(60), Now);

        var entry = LedgerEntry.Transfer(order, LedgerAccount.OwnerFunds, LedgerAccount.Escrow, 60m, LedgerEntryKind.Hold, "  下单托管  ", Now);

        Assert.Equal(order.Id, entry.OrderId);
        Assert.Equal(order.TaskId, entry.TaskId);
        Assert.Equal(LedgerAccount.OwnerFunds, entry.DebitAccount);
        Assert.Equal(LedgerAccount.Escrow, entry.CreditAccount);
        Assert.Equal(60m, entry.Amount);
        Assert.Equal("CNY", entry.Currency);
        Assert.Equal(LedgerEntryKind.Hold, entry.Kind);
        Assert.Equal("下单托管", entry.Note);
        Assert.Equal(Now, entry.OccurredAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void The_amount_must_be_positive(decimal amount)
    {
        Assert.Throws<DomainException>(() => LedgerEntry.Record(Guid.NewGuid(), Guid.NewGuid(), LedgerAccount.Escrow, LedgerAccount.OwnerFunds, amount, "CNY", LedgerEntryKind.Refund, null, Now));
    }

    [Fact]
    public void The_two_accounts_must_differ_and_the_currency_must_be_known()
    {
        Assert.Throws<DomainException>(() => LedgerEntry.Record(Guid.NewGuid(), Guid.NewGuid(), LedgerAccount.Escrow, LedgerAccount.Escrow, 10m, "CNY", LedgerEntryKind.Hold, null, Now));
        Assert.Throws<DomainException>(() => LedgerEntry.Record(Guid.NewGuid(), Guid.NewGuid(), LedgerAccount.Escrow, LedgerAccount.OwnerFunds, 10m, "  ", LedgerEntryKind.Refund, null, Now));
        Assert.Throws<DomainException>(() => LedgerEntry.Record(Guid.Empty, Guid.NewGuid(), LedgerAccount.Escrow, LedgerAccount.OwnerFunds, 10m, "CNY", LedgerEntryKind.Refund, null, Now));
    }

    [Fact]
    public void A_very_long_note_is_truncated_instead_of_failing()
    {
        var entry = LedgerEntry.Record(Guid.NewGuid(), Guid.NewGuid(), LedgerAccount.Escrow, LedgerAccount.WorkerPayout, 10m, "cny", LedgerEntryKind.Release, new string('长', 500), Now);

        Assert.Equal(LedgerEntry.MaxNoteLength, entry.Note!.Length);
        // 币种统一成大写，避免同一币种写成两种形式。
        Assert.Equal("CNY", entry.Currency);
    }
}

/// <summary>测试里拼装"已经冻结"的订单用的小工具（避免每个用例重复写四行）。</summary>
internal static class OrderEscrowTestExtensions
{
    public static Order Settle(this Order order, decimal workerAmount, decimal ownerAmount)
    {
        order.SettleEscrow(workerAmount, ownerAmount, DateTimeOffset.UtcNow);
        return order;
    }
}
