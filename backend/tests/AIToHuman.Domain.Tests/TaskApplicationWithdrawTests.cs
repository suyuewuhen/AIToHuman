using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>报名截止时间与“服务者撤回报名”的领域规则。</summary>
public sealed class TaskApplicationWithdrawTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Worker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherWorker = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Application_deadline_must_be_after_creation_and_not_later_than_the_task_deadline()
    {
        Assert.Throws<DomainException>(() => Create(applicationDeadline: Now));
        Assert.Throws<DomainException>(() => Create(applicationDeadline: Now.AddDays(2)));

        var task = Create(applicationDeadline: Now.AddHours(2));
        Assert.Equal(Now.AddHours(2), task.ApplicationDeadline);
    }

    [Fact]
    public void Applications_are_refused_after_the_application_deadline()
    {
        var task = Create(applicationDeadline: Now.AddHours(1));
        task.Publish(Now);
        task.Apply(Worker, null, Now.AddMinutes(30));

        Assert.True(task.AcceptingApplications(Now.AddMinutes(30)));
        Assert.False(task.AcceptingApplications(Now.AddHours(2)));

        var error = Assert.Throws<DomainException>(() => task.Apply(OtherWorker, null, Now.AddHours(2)));

        Assert.Contains("报名已经截止", error.Message);
        // 已报名的记录仍然有效，需求方照样可以选中它。
        Assert.Equal(TaskApplicationStatus.Pending, task.Applications.Single(item => item.WorkerId == Worker).Status);
        Assert.Equal(Worker, task.SelectApplication(task.Applications.Single().Id).WorkerId);
    }

    [Fact]
    public void Publishing_is_refused_when_the_application_deadline_already_passed()
    {
        var task = Create(applicationDeadline: Now.AddMinutes(30));

        var error = Assert.Throws<DomainException>(() => task.Publish(Now.AddHours(1)));

        Assert.Contains("报名截止时间已过", error.Message);
        Assert.Equal(TaskStatus.ReadyToPublish, task.Status);
    }

    [Fact]
    public void Applicants_can_withdraw_their_pending_application_and_apply_again()
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, "半小时可到", Now);

        var withdrawn = task.WithdrawApplication(application.Id, Worker, Now.AddMinutes(5));

        Assert.Equal(TaskApplicationStatus.Withdrawn, withdrawn.Status);
        Assert.Equal(TaskApplicationStatus.Withdrawn, task.Applications.Single().Status);

        // 撤回只是作废这一条：服务者可以再报一次，记录保留便于追溯。
        var again = task.Apply(Worker, "又想接了", Now.AddMinutes(10));
        Assert.Equal(TaskApplicationStatus.Pending, again.Status);
        Assert.Equal(2, task.Applications.Count);
    }

    [Fact]
    public void Only_the_owner_of_the_application_can_withdraw_it()
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);

        var error = Assert.Throws<UnauthorizedAccessException>(() => task.WithdrawApplication(application.Id, OtherWorker, Now));

        Assert.Contains("只能撤回自己的报名", error.Message);
        Assert.Equal(TaskApplicationStatus.Pending, application.Status);
    }

    [Fact]
    public void Unknown_or_already_handled_applications_cannot_be_withdrawn()
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);

        Assert.Throws<DomainException>(() => task.WithdrawApplication(Guid.NewGuid(), Worker, Now));

        task.WithdrawApplication(application.Id, Worker, Now);
        var error = Assert.Throws<DomainException>(() => task.WithdrawApplication(application.Id, Worker, Now));
        Assert.Contains("只有待处理的报名可以撤回", error.Message);
    }

    [Fact]
    public void Selected_applications_cannot_be_withdrawn()
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);

        var error = Assert.Throws<DomainException>(() => task.WithdrawApplication(application.Id, Worker, Now));

        Assert.Contains("只有待处理的报名可以撤回", error.Message);
        Assert.Equal(TaskStatus.Assigned, task.Status);
    }

    [Fact]
    public void Withdrawal_still_works_after_the_task_was_withdrawn_by_its_owner()
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.Cancel("不需要人帮忙了", Now);

        // 任务被撤销不等于报名记录要一直挂着“待处理”。
        task.WithdrawApplication(application.Id, Worker, Now.AddMinutes(1));
        Assert.Equal(TaskApplicationStatus.Withdrawn, application.Status);
    }

    [Fact]
    public void Application_deadline_survives_rehydration()
    {
        var rehydrated = TaskItem.Rehydrate(
            Guid.NewGuid(), Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now,
            TaskStatus.Published, [], executionAddress: null, expiredAt: null, cancelledAt: null, cancellationReason: null,
            applicationDeadline: Now.AddHours(2));

        Assert.Equal(Now.AddHours(2), rehydrated.ApplicationDeadline);
    }

    private static TaskItem Create(DateTimeOffset? applicationDeadline = null) =>
        new(Owner, "代取文件", "到前台取件", "朝阳区", Now.AddHours(6), new Money(50), ["按时送达"], Now,
            executionAddress: null, applicationDeadline: applicationDeadline);
}
