using AIToHuman.Domain.Common;
using AIToHuman.Domain.Tasks;
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Domain.Tests;

public sealed class TaskItemTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 9, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Published_task_can_only_increase_reward()
    {
        var task = CreateTask();
        task.Publish(_now);
        task.IncreaseReward(new Money(75));
        Assert.Equal(75, task.Reward.Amount);
        Assert.Throws<DomainException>(() => task.IncreaseReward(new Money(70)));
    }

    [Fact]
    public void Owner_cannot_apply_for_own_task()
    {
        var task = CreateTask();
        task.Publish(_now);
        Assert.Throws<DomainException>(() => task.Apply(task.OwnerId, null, _now));
    }

    [Fact]
    public void Worker_cannot_submit_duplicate_pending_application()
    {
        var task = CreateTask();
        task.Publish(_now);
        var workerId = Guid.NewGuid();

        task.Apply(workerId, "第一次报名", _now);

        var error = Assert.Throws<DomainException>(() => task.Apply(workerId, "重复报名", _now.AddMinutes(1)));
        Assert.Equal("服务者已经报名该任务。", error.Message);
    }

    [Fact]
    public void Selecting_application_assigns_task_and_rejects_others()
    {
        var task = CreateTask();
        task.Publish(_now);
        var selected = task.Apply(Guid.NewGuid(), "半小时可到", _now);
        var other = task.Apply(Guid.NewGuid(), "一小时可到", _now);
        task.SelectApplication(selected.Id);
        Assert.Equal(TaskStatus.Assigned, task.Status);
        Assert.Equal(TaskApplicationStatus.Selected, selected.Status);
        Assert.Equal(TaskApplicationStatus.Rejected, other.Status);
    }

    [Fact]
    public void Assigned_task_closes_after_order_approval()
    {
        var task = CreateTask();
        task.Publish(_now);
        var selected = task.Apply(Guid.NewGuid(), "半小时可到", _now);
        task.SelectApplication(selected.Id);

        task.Close();

        Assert.Equal(TaskStatus.Closed, task.Status);
    }

    [Fact]
    public void Task_cannot_close_before_an_order_is_assigned()
    {
        var task = CreateTask();
        task.Publish(_now);

        Assert.Throws<DomainException>(() => task.Close());
    }

    [Fact]
    public void Deadline_with_local_offset_is_stored_as_utc()
    {
        var createdAt = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var deadline = new DateTimeOffset(2026, 9, 10, 20, 0, 0, TimeSpan.FromHours(8));

        var task = new TaskItem(Guid.NewGuid(), "代取文件", "从前台取一份普通文件", "浦东新区", deadline, new Money(50), ["上传取件码照片"], createdAt);

        Assert.Equal(TimeSpan.Zero, task.Deadline.Offset);
        Assert.Equal(deadline.ToUniversalTime(), task.Deadline);
        Assert.Equal(createdAt, task.CreatedAt);
    }

    [Fact]
    public void Deadline_comparison_uses_absolute_time_across_offsets()
    {
        var createdAt = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var sameInstantInLocalOffset = createdAt.ToOffset(TimeSpan.FromHours(8));

        Assert.Throws<DomainException>(() => new TaskItem(Guid.NewGuid(), "代取文件", "从前台取一份普通文件", "浦东新区", sameInstantInLocalOffset, new Money(50), ["上传取件码照片"], createdAt));
    }

    [Fact]
    public void Execution_address_is_optional_and_length_checked()
    {
        var taskWithAddress = new TaskItem(Guid.NewGuid(), "代取文件", "从前台取一份普通文件", "浦东新区", _now.AddHours(6), new Money(50), ["完成"], _now, "  上海市浦东新区世纪大道 100 号前台  ");
        var taskWithoutAddress = CreateTask();

        Assert.Equal("上海市浦东新区世纪大道 100 号前台", taskWithAddress.ExecutionAddress);
        Assert.True(taskWithAddress.HasExecutionAddress);
        Assert.Null(taskWithoutAddress.ExecutionAddress);
        Assert.False(taskWithoutAddress.HasExecutionAddress);
        Assert.Throws<DomainException>(() => new TaskItem(Guid.NewGuid(), "代取文件", "说明", "浦东新区", _now.AddHours(6), new Money(50), ["完成"], _now, new string('址', 201)));
    }

    [Fact]
    public void Execution_address_is_disclosed_only_to_the_owner_and_the_selected_worker()
    {
        var owner = Guid.NewGuid();
        var selectedWorker = Guid.NewGuid();
        var otherWorker = Guid.NewGuid();
        var task = new TaskItem(owner, "代取文件", "从前台取一份普通文件", "浦东新区", _now.AddHours(6), new Money(50), ["完成"], _now, "世纪大道 100 号前台");

        // 未分配前：除所有者外任何人都拿不到。
        Assert.Equal("世纪大道 100 号前台", task.ExecutionAddressFor(owner));
        Assert.Null(task.ExecutionAddressFor(selectedWorker));
        Assert.Null(task.ExecutionAddressFor(Guid.Empty));
        Assert.Null(task.ExecutionAddressFor(null));

        task.Publish(_now);
        var selected = task.Apply(selectedWorker, "半小时可到", _now);
        var other = task.Apply(otherWorker, "一小时可到", _now);
        task.SelectApplication(selected.Id);

        Assert.Equal("世纪大道 100 号前台", task.ExecutionAddressFor(owner));
        Assert.Equal("世纪大道 100 号前台", task.ExecutionAddressFor(selectedWorker));
        // 报名但未被选中的服务者依然看不到精确地址。
        Assert.Null(task.ExecutionAddressFor(other.WorkerId));
    }

    private TaskItem CreateTask() => new(Guid.NewGuid(), "代取文件", "从前台取一份普通文件", "浦东新区", _now.AddHours(6), new Money(50), ["上传取件码已核销的照片"], _now.AddMinutes(-1));
}
