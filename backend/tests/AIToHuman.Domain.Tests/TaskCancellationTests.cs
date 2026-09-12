using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

/// <summary>运营人工下架与运营审计的领域规则。</summary>
public sealed class TaskCancellationTests
{
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Worker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Admin = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Draft_and_published_tasks_can_be_taken_down()
    {
        var draft = Create();
        draft.Cancel("包含违规内容", Now);
        Assert.Equal(TaskStatus.Cancelled, draft.Status);

        var published = Create();
        published.Publish(Now);
        published.Cancel("运营人工复核不通过", Now);
        Assert.Equal(TaskStatus.Cancelled, published.Status);
    }

    [Fact]
    public void Assigned_tasks_cannot_be_taken_down_before_the_order_is_resolved()
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);

        var error = Assert.Throws<DomainException>(() => task.Cancel("想直接下架", Now));

        Assert.Contains("已经分配并产生订单", error.Message);
        Assert.Equal(TaskStatus.Assigned, task.Status);
    }

    [Fact]
    public void Closed_tasks_cannot_be_taken_down() 
    {
        var task = Create();
        task.Publish(Now);
        var application = task.Apply(Worker, null, Now);
        task.SelectApplication(application.Id);
        task.Close();

        Assert.Throws<DomainException>(() => task.Cancel("下架已结束的任务", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Takedown_requires_a_reason(string reason)
    {
        var task = Create();

        var error = Assert.Throws<DomainException>(() => task.Cancel(reason, Now));

        Assert.Contains("必须填写原因", error.Message);
        Assert.Equal(TaskStatus.ReadyToPublish, task.Status);
    }

    [Fact]
    public void Takedown_reason_is_bounded() =>
        Assert.Throws<DomainException>(() => Create().Cancel(new string('x', TaskItem.MaxCancellationReasonLength + 1), Now));

    [Fact]
    public void Audit_entry_records_actor_target_and_reason()
    {
        var target = Guid.NewGuid();
        var entry = AdminAuditEntry.Record(Admin, AdminConsoleActionName, "task", target, "包含违规内容", Now.AddHours(8));

        Assert.Equal(Admin, entry.ActorId);
        Assert.Equal("task", entry.TargetType);
        Assert.Equal(target, entry.TargetId);
        Assert.Equal("包含违规内容", entry.Reason);
        // 时间统一归一化为 UTC。
        Assert.Equal(TimeSpan.Zero, entry.OccurredAt.Offset);
    }

    [Fact]
    public void Audit_entry_requires_actor_target_and_reason()
    {
        var target = Guid.NewGuid();
        Assert.Throws<DomainException>(() => AdminAuditEntry.Record(Guid.Empty, AdminConsoleActionName, "task", target, "原因", Now));
        Assert.Throws<DomainException>(() => AdminAuditEntry.Record(Admin, AdminConsoleActionName, "task", Guid.Empty, "原因", Now));
        Assert.Throws<DomainException>(() => AdminAuditEntry.Record(Admin, AdminConsoleActionName, "task", target, "  ", Now));
    }

    private const string AdminConsoleActionName = "task.cancel";

    private static TaskItem Create() => new(
        Owner,
        "帮忙取一份文件",
        "到指定地点取件并送达",
        "朝阳区",
        Now.AddDays(1),
        new Money(50),
        ["按时送达"],
        Now);
}
