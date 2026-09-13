using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Payments;
using AIToHuman.Application.Settings;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Payments;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Payments;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 资金托管的用例层行为：下单冻结、验收放款、取消退款、争议分账，以及两条边界——
/// 网关不通时状态不变（fail closed），托管关掉时行为与没有托管时一致。
/// </summary>
public sealed class EscrowPaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Selecting_a_worker_freezes_the_reward_and_writes_one_ledger_entry()
    {
        var world = new World();

        var order = world.SelectOrder(reward: 60);

        Assert.Equal("Held", order.EscrowStatus);
        Assert.Equal(60m, order.EscrowAmount);
        Assert.Equal(0m, order.ReleasedAmount);
        Assert.NotNull(order.EscrowHeldAt);
        Assert.StartsWith("sim-hold-", order.PaymentReference);

        var entry = Assert.Single(world.Ledger.ListByOrder(order.Id));
        Assert.Equal(LedgerEntryKind.Hold, entry.Kind);
        Assert.Equal(LedgerAccount.OwnerFunds, entry.DebitAccount);
        Assert.Equal(LedgerAccount.Escrow, entry.CreditAccount);
        Assert.Equal(60m, entry.Amount);
    }

    [Fact]
    public void Approving_releases_the_escrow_to_the_worker()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 60);
        world.WorkerStartAndSubmit(order.Id);

        var approved = world.Tasks.ApproveOrder(order.Id, world.Owner, "干得不错");

        Assert.Equal("Approved", approved.Status);
        Assert.Equal("Released", approved.EscrowStatus);
        Assert.Equal(60m, approved.ReleasedAmount);
        Assert.Equal(0m, approved.RefundedAmount);

        var entries = world.Ledger.ListByOrder(order.Id);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, item => item.Kind == LedgerEntryKind.Hold);
        Assert.Contains(entries, item => item.Kind == LedgerEntryKind.Release && item.CreditAccount == LedgerAccount.WorkerPayout && item.Amount == 60m);
    }

    [Fact]
    public void Cancelling_refunds_the_escrow_to_the_owner()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 60);

        var cancelled = world.Tasks.CancelOrder(order.Id, world.Owner, "临时不需要了");

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Refunded", cancelled.EscrowStatus);
        Assert.Equal(60m, cancelled.RefundedAmount);

        var entries = world.Ledger.ListByOrder(order.Id);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, item => item.Kind == LedgerEntryKind.Refund && item.CreditAccount == LedgerAccount.OwnerFunds && item.Amount == 60m);
    }

    [Fact]
    public void A_dispute_can_split_the_escrow_between_the_two_parties()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 100);
        world.WorkerStartAndSubmit(order.Id);
        world.Tasks.OpenDispute(order.Id, world.Owner, "和描述不符");

        var resolved = world.Admin.ResolveDispute(order.Id, "Approve", "按完成一半结算", world.AdminId, amount: 40m);

        Assert.Equal("Approved", resolved.Status);
        Assert.Equal("Settled", resolved.EscrowStatus);
        Assert.Equal(40m, resolved.ReleasedAmount);
        Assert.Equal(60m, resolved.RefundedAmount);

        var entries = world.Ledger.ListByOrder(order.Id);
        // 三笔：冻结 + 部分放款 + 部分退款——"一半赔付"在流水里是看得见的。
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, item => item.Kind == LedgerEntryKind.PartialRelease && item.Amount == 40m);
        Assert.Contains(entries, item => item.Kind == LedgerEntryKind.PartialRefund && item.Amount == 60m);
    }

    [Fact]
    public void Settling_more_than_the_escrow_is_rejected()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 100);
        world.WorkerStartAndSubmit(order.Id);
        world.Tasks.OpenDispute(order.Id, world.Owner, "和描述不符");

        var error = Assert.Throws<DomainException>(() => world.Admin.ResolveDispute(order.Id, "Approve", "多赔一点", world.AdminId, amount: 120m));

        Assert.Contains("必须在 0 到托管金额", error.Message);
        // 没有产生任何新的流水（"状态与钱一起回滚"这件事在真库用例里断言，内存装配不做事务）。
        Assert.Single(world.Ledger.ListByOrder(order.Id));
    }

    [Fact]
    public void Rework_in_a_dispute_moves_no_money()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 100);
        world.WorkerStartAndSubmit(order.Id);
        world.Tasks.OpenDispute(order.Id, world.Owner, "和描述不符");

        var resolved = world.Admin.ResolveDispute(order.Id, "Rework", "再补做一次", world.AdminId);

        Assert.Equal("InProgress", resolved.Status);
        // 钱继续冻着，等下一轮结论。
        Assert.Equal("Held", resolved.EscrowStatus);
        Assert.Equal(100m, world.Orders.Get(order.Id)!.EscrowBalance);
        Assert.Single(world.Ledger.ListByOrder(order.Id));
    }

    [Fact]
    public void Rework_cannot_carry_an_amount_at_all()
    {
        // 退回返工这一档不涉及资金：运营填了金额就是误解了语义，必须当场拒绝。
        var world = new World();
        var order = world.SelectOrder(reward: 100);
        world.WorkerStartAndSubmit(order.Id);
        world.Tasks.OpenDispute(order.Id, world.Owner, "和描述不符");

        var error = Assert.Throws<DomainException>(() => world.Admin.ResolveDispute(order.Id, "Rework", "再补做一次", world.AdminId, amount: 10m));

        Assert.Contains("退回返工不涉及资金", error.Message);
        Assert.Single(world.Ledger.ListByOrder(order.Id));
    }

    [Fact]
    public void A_failed_hold_blocks_the_selection_and_writes_no_ledger_entry()
    {
        var world = new World();
        world.Gateway.FailureMode = SimulatedPaymentAction.Hold;
        var taskId = world.Publish(reward: 60);
        var applicationId = world.Apply(taskId);

        var error = Assert.Throws<DomainException>(() => world.Tasks.Select(taskId, applicationId, new SelectApplicationRequest(world.Owner)));

        Assert.Contains("资金托管失败", error.Message);
        // 没有流水：托管没成功就绝不会有"钱冻住了"的记录。
        // "任务与订单也一起回滚"由真库用例断言——内存装配不做事务，这里断言不了。
        Assert.Empty(world.Ledger.ListRecent(10));
    }

    [Fact]
    public void A_failed_release_writes_no_ledger_entry_and_keeps_the_escrow_frozen()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 60);
        world.WorkerStartAndSubmit(order.Id);
        world.Gateway.FailureMode = SimulatedPaymentAction.Capture;

        var error = Assert.Throws<DomainException>(() => world.Tasks.ApproveOrder(order.Id, world.Owner, "通过"));

        Assert.Contains("放款失败", error.Message);
        // 钱没有被放出去：流水里只有那笔冻结。
        var entry = Assert.Single(world.Ledger.ListByOrder(order.Id));
        Assert.Equal(LedgerEntryKind.Hold, entry.Kind);
    }

    [Fact]
    public void Turning_escrow_off_keeps_the_behaviour_without_money()
    {
        var world = new World(escrowEnabled: false);

        var order = world.SelectOrder(reward: 60);

        Assert.Equal("None", order.EscrowStatus);
        Assert.Equal(0m, order.EscrowAmount);
        Assert.Empty(world.Ledger.ListByOrder(order.Id));
        // 状态流转照常。
        Assert.Equal("Accepted", order.Status);
    }

    [Fact]
    public void Only_the_two_parties_can_read_the_ledger()
    {
        var world = new World();
        var order = world.SelectOrder(reward: 60);

        Assert.Single(world.Tasks.ListOrderLedger(order.Id, world.Owner));
        Assert.Single(world.Tasks.ListOrderLedger(order.Id, world.Worker));
        Assert.Throws<UnauthorizedAccessException>(() => world.Tasks.ListOrderLedger(order.Id, Guid.NewGuid()));
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();
        private readonly InMemoryAdminAuditRepository audits = new();
        private readonly InMemoryUnitOfWork unitOfWork = new();
        private readonly InMemoryNotificationRepository notifications = new();

        public World(bool escrowEnabled = true)
        {
            Owner = Guid.NewGuid();
            Worker = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Clock = new FixedTimeProvider(Now);
            Ledger = new InMemoryLedgerRepository();
            Gateway = new SimulatedPaymentGateway();
            Settings = new TestSettingsProvider(new Dictionary<string, string?>
            {
                [SettingKeys.PaymentProvider] = escrowEnabled ? SettingKeys.PaymentProviderSimulated : SettingKeys.PaymentProviderDisabled
            });

            var notificationService = new NotificationService(notifications, Clock);
            Payments = new PaymentService(Gateway, Settings, Ledger, Clock, unitOfWork);
            Tasks = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(),
                new EmptyUserDirectory(), Clock, notificationService, unitOfWork, payments: Payments);
            Admin = new AdminConsoleService(tasks, orders, new EmptyUserDirectory(), tasks, orders, audits, Clock, notificationService, unitOfWork, Payments);
        }

        public Guid Owner { get; }
        public Guid Worker { get; }
        public Guid AdminId { get; }
        public FixedTimeProvider Clock { get; }
        public SimulatedPaymentGateway Gateway { get; }
        public TestSettingsProvider Settings { get; }
        public InMemoryLedgerRepository Ledger { get; }
        public PaymentService Payments { get; }
        public TaskService Tasks { get; }
        public AdminConsoleService Admin { get; }
        public InMemoryTaskRepository TaskStore => tasks;
        public InMemoryOrderRepository OrderStore => orders;

        /// <summary>把 Repository 也暴露出来，断言"任务/订单/流水一起回滚"时用。</summary>
        public ITaskRepository TaskRepository => tasks;
        public IOrderRepository Orders => orders;

        public Guid Publish(decimal reward)
        {
            var task = Tasks.Create(new CreateTaskRequest(Owner, "代取文件", "到前台取一份普通文件", "浦东新区", Now.AddHours(6), reward, ["完成"], "世纪大道 100 号前台"));
            Tasks.Publish(task.Id, Owner);
            return task.Id;
        }

        public Guid Apply(Guid taskId) => Tasks
            .Apply(taskId, new ApplyForTaskRequest(Worker, "半小时可到"))
            .Applications.Single(item => item.Status == "Pending").Id;

        /// <summary>发布 → 报名 → 选人，返回订单响应（选人这一步会冻结资金）。</summary>
        public OrderResponse SelectOrder(decimal reward)
        {
            var taskId = Publish(reward);
            var applicationId = Apply(taskId);
            return Tasks.Select(taskId, applicationId, new SelectApplicationRequest(Owner)).Order;
        }

        public void WorkerStartAndSubmit(Guid orderId)
        {
            Tasks.StartOrder(orderId, Worker);
            Tasks.SubmitOrder(orderId, Worker, "已完成，凭证见照片");
        }
    }
}
