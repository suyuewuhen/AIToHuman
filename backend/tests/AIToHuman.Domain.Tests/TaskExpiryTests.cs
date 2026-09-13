using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>任务过期与「订单取消后任务回到大厅或过期」的领域规则。</summary>
public sealed class TaskExpiryTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Worker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherWorker = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Published_task_past_its_deadline_expires_and_invalidates_pending_applications()
    {
        var task = Create(deadline: Now.AddHours(1));
        task.Publish(Now);
        task.Apply(Worker, null, Now);
        task.Apply(OtherWorker, null, Now);

        var affected = task.Expire(Now.AddHours(2));

        Assert.Equal(TaskStatus.Expired, task.Status);
        Assert.Equal(Now.AddHours(2), task.ExpiredAt);
        Assert.Equal(2, affected.Count);
        Assert.Contains(Worker, affected);
        Assert.Contains(OtherWorker, affected);
        Assert.All(task.Applications, item => Assert.Equal(TaskApplicationStatus.Expired, item.Status));
    }

    [Fact]
    public void Task_cannot_expire_before_its_deadline()
    {
        var task = Create(deadline: Now.AddHours(3));
        task.Publish(Now);

        var error = Assert.Throws<DomainException>(() => task.Expire(Now.AddHours(1)));

        Assert.Contains("截止时间还没到", error.Message);
        Assert.Equal(TaskStatus.Published, task.Status);
        Assert.Null(task.ExpiredAt);
    }

    [Fact]
    public void Assigned_tasks_are_not_expired_by_the_scanner()
    {
        var task = Create(deadline: Now.AddHours(1));
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);

        // 已产生订单的任务归订单流程管：不能因为过了截止时间就把它从服务者名下拿走。
        Assert.Throws<DomainException>(() => task.Expire(Now.AddHours(2)));
        Assert.Equal(TaskStatus.Assigned, task.Status);
    }

    [Fact]
    public void Cancelling_the_order_reopens_the_task_when_the_deadline_has_not_passed()
    {
        var task = Create(deadline: Now.AddHours(6));
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);

        var outcome = task.ReleaseAfterOrderCancelled(Now.AddHours(1));

        Assert.Equal(TaskReleaseOutcome.Reopened, outcome);
        Assert.Equal(TaskStatus.Published, task.Status);
        Assert.Null(task.ExpiredAt);
        // 该次选择作废，服务者可以重新报名。
        Assert.Equal(TaskApplicationStatus.Rejected, task.Applications.Single().Status);
        Assert.NotNull(task.Apply(Worker, "重新报名", Now.AddHours(1)));
    }

    [Fact]
    public void Reopening_also_withdraws_the_execution_address_from_the_deselected_worker()
    {
        var task = new TaskItem(
            Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now, "建国路 1 号 101 室");
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);
        Assert.Equal("建国路 1 号 101 室", task.ExecutionAddressFor(Worker));

        task.ReleaseAfterOrderCancelled(Now.AddHours(1));

        // 作废选中报名后，执行地址不再对原服务者披露。
        Assert.Null(task.ExecutionAddressFor(Worker));
        Assert.Equal("建国路 1 号 101 室", task.ExecutionAddressFor(Owner));
    }

    [Fact]
    public void Cancelling_the_order_after_the_deadline_expires_the_task()
    {
        var task = Create(deadline: Now.AddHours(1));
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);

        var outcome = task.ReleaseAfterOrderCancelled(Now.AddHours(2));

        Assert.Equal(TaskReleaseOutcome.Expired, outcome);
        Assert.Equal(TaskStatus.Expired, task.Status);
        Assert.Equal(Now.AddHours(2), task.ExpiredAt);
        Assert.Equal(TaskApplicationStatus.Rejected, task.Applications.Single().Status);
    }

    [Fact]
    public void Only_assigned_tasks_can_be_released_by_a_cancelled_order()
    {
        var task = Create(deadline: Now.AddHours(6));
        task.Publish(Now);

        Assert.Throws<DomainException>(() => task.ReleaseAfterOrderCancelled(Now));
    }

    [Fact]
    public void Cancelling_a_task_records_the_reason_and_time()
    {
        var task = Create(deadline: Now.AddHours(6));
        task.Publish(Now);

        task.Cancel("  不再需要人帮忙了  ", Now.AddMinutes(3));

        Assert.Equal(TaskStatus.Cancelled, task.Status);
        Assert.Equal(Now.AddMinutes(3), task.CancelledAt);
        Assert.Equal("不再需要人帮忙了", task.CancellationReason);
        // 撤销后不再接受报名。
        Assert.Throws<DomainException>(() => task.Apply(Worker, null, Now.AddMinutes(4)));
    }

    [Fact]
    public void Rehydrate_keeps_expiry_and_cancellation_trail()
    {
        var task = TaskItem.Rehydrate(
            Guid.NewGuid(), Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(1), new Money(50), ["按时送达"], Now,
            TaskStatus.Expired, [], executionAddress: null, expiredAt: Now.AddHours(2), cancelledAt: null, cancellationReason: null);

        Assert.Equal(TaskStatus.Expired, task.Status);
        Assert.Equal(Now.AddHours(2), task.ExpiredAt);
        Assert.Null(task.CancelledAt);
    }

    private static TaskItem Create(DateTimeOffset deadline) =>
        new(Owner, "代取文件", "到前台取件", "朝阳区", deadline, new Money(50), ["按时送达"], Now);
}
