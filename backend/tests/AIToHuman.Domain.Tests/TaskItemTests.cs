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

    private TaskItem CreateTask() => new(Guid.NewGuid(), "代取文件", "从前台取一份普通文件", "浦东新区", _now.AddHours(6), new Money(50), ["上传取件码已核销的照片"], _now.AddMinutes(-1));
}
