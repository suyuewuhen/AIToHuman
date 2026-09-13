using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 精确地址访问留痕的领域规则：身份与结论都由任务本身推出（不信任调用方传进来的角色），
/// 且"没有登记地址"与"越权尝试"要区分开——前者不是探测，后者才是。
/// </summary>
public sealed class AddressAccessEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid SelectedWorker = Guid.NewGuid();
    private static readonly Guid OtherWorker = Guid.NewGuid();

    [Fact]
    public void The_owner_is_recorded_as_disclosed_and_the_stranger_as_denied()
    {
        var task = Selected("世纪大道 100 号前台");

        var ownerEntry = AddressAccessEntry.Record(task, Owner, Now);
        var strangerEntry = AddressAccessEntry.Record(task, Guid.NewGuid(), Now);

        Assert.Equal(AddressAccessRole.Owner, ownerEntry.Role);
        Assert.Equal(AddressAccessOutcome.Granted, ownerEntry.Outcome);
        Assert.True(ownerEntry.Disclosed);

        Assert.Equal(AddressAccessRole.Other, strangerEntry.Role);
        Assert.Equal(AddressAccessOutcome.Denied, strangerEntry.Outcome);
        Assert.False(strangerEntry.Disclosed);
    }

    [Fact]
    public void Only_the_selected_worker_counts_as_a_participant()
    {
        var task = Selected("世纪大道 100 号前台");

        // 被选中的服务者：披露。
        Assert.Equal(AddressAccessOutcome.Granted, AddressAccessEntry.Record(task, SelectedWorker, Now).Outcome);
        // 同一条任务上"报了名但没被选中"的服务者：拒绝。
        Assert.Equal(AddressAccessOutcome.Denied, AddressAccessEntry.Record(task, OtherWorker, Now).Outcome);
    }

    [Fact]
    public void An_anonymous_read_is_denied_and_keeps_no_viewer_id()
    {
        var task = Selected("世纪大道 100 号前台");

        var anonymous = AddressAccessEntry.Record(task, null, Now);
        var emptyGuid = AddressAccessEntry.Record(task, Guid.Empty, Now);

        Assert.Null(anonymous.ViewerId);
        Assert.Equal(AddressAccessOutcome.Denied, anonymous.Outcome);
        // 传入 Guid.Empty 与传 null 是同一件事：都按匿名处理，不要把它当成一个真实用户记下来。
        Assert.Null(emptyGuid.ViewerId);
        Assert.Equal(AddressAccessOutcome.Denied, emptyGuid.Outcome);
    }

    [Fact]
    public void A_task_without_an_address_is_not_a_denied_attempt()
    {
        var task = Selected(null);

        var ownerEntry = AddressAccessEntry.Record(task, Owner, Now);
        var strangerEntry = AddressAccessEntry.Record(task, Guid.NewGuid(), Now);

        // 没有地址可披露：无论谁来看都记成 NotSet，绝不算成"越权尝试"（否则统计会失真）。
        Assert.Equal(AddressAccessOutcome.NotSet, ownerEntry.Outcome);
        Assert.Equal(AddressAccessOutcome.NotSet, strangerEntry.Outcome);
        Assert.False(ownerEntry.Disclosed);
        Assert.False(strangerEntry.Disclosed);
    }

    [Fact]
    public void The_record_always_carries_the_task_and_the_instant()
    {
        var task = Selected("世纪大道 100 号前台");

        var entry = AddressAccessEntry.Record(task, Owner, Now);

        Assert.Equal(task.Id, entry.TaskId);
        Assert.Equal(Owner, entry.ViewerId);
        Assert.Equal(Now, entry.OccurredAt);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    private static TaskItem Selected(string? executionAddress)
    {
        var task = new TaskItem(Owner, "代取文件", "到前台取一份普通文件", "浦东新区", Now.AddHours(6), new Money(50), ["完成"], Now, executionAddress);
        task.Publish(Now);
        var selected = task.Apply(SelectedWorker, "半小时可到", Now);
        task.Apply(OtherWorker, "一小时可到", Now);
        task.SelectApplication(selected.Id);
        return task;
    }
}
