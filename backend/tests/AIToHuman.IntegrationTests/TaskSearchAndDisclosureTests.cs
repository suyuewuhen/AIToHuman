using AIToHuman.Application.Notifications;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>大厅列表的分页与筛选、公开详情权限，以及精确地址的分阶段披露。</summary>
public sealed class TaskSearchAndDisclosureTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Paging_walks_the_hall_in_deadline_order_without_gaps_or_repeats()
    {
        var (service, owner) = CreateService();
        var ids = new List<Guid>();
        for (var index = 0; index < 5; index++) ids.Add(Publish(service, owner, $"任务 {index}", "浦东新区", 30, Now.AddHours(index + 2)));

        var first = service.SearchPublished(new TaskSearchRequest(null, null, null, 2, null));
        var second = service.SearchPublished(new TaskSearchRequest(null, null, null, 2, first.NextCursor));
        var third = service.SearchPublished(new TaskSearchRequest(null, null, null, 2, second.NextCursor));

        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        Assert.Equal(2, second.Items.Count);
        Assert.True(second.HasMore);
        Assert.Single(third.Items);
        Assert.False(third.HasMore);
        Assert.Null(third.NextCursor);

        var seen = first.Items.Concat(second.Items).Concat(third.Items).Select(item => item.Id).ToArray();
        Assert.Equal(ids, seen);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public void Paging_keeps_tasks_with_the_same_deadline_stable()
    {
        var (service, owner) = CreateService();
        for (var index = 0; index < 3; index++) Publish(service, owner, $"同刻 {index}", "浦东新区", 30, Now.AddHours(3));

        var first = service.SearchPublished(new TaskSearchRequest(null, null, null, 2, null));
        var second = service.SearchPublished(new TaskSearchRequest(null, null, null, 2, first.NextCursor));

        var seen = first.Items.Concat(second.Items).Select(item => item.Id).ToArray();

        Assert.Equal(3, seen.Length);
        Assert.Equal(3, seen.Distinct().Count());
    }

    [Fact]
    public void Filters_apply_district_and_reward_range()
    {
        var (service, owner) = CreateService();
        Publish(service, owner, "浦东低价", "浦东新区", 20, Now.AddHours(2));
        Publish(service, owner, "浦东中价", "浦东新区", 50, Now.AddHours(3));
        Publish(service, owner, "徐汇中价", "徐汇区", 50, Now.AddHours(4));

        var byDistrict = service.SearchPublished(new TaskSearchRequest("浦东新区", null, null, 10, null));
        var byRange = service.SearchPublished(new TaskSearchRequest(null, 40, 60, 10, null));
        var combined = service.SearchPublished(new TaskSearchRequest("浦东新区", 40, 60, 10, null));

        Assert.Equal(2, byDistrict.Items.Count);
        Assert.All(byDistrict.Items, item => Assert.Equal("浦东新区", item.District));
        Assert.Equal(2, byRange.Items.Count);
        Assert.All(byRange.Items, item => Assert.InRange(item.Reward, 40, 60));
        Assert.Single(combined.Items);
        Assert.Equal("浦东中价", combined.Items[0].Title);
    }

    [Fact]
    public void Drafts_are_not_listed_in_the_hall()
    {
        var (service, owner) = CreateService();
        var draft = service.Create(new CreateTaskRequest(owner, "草稿任务", "还没发布", "浦东新区", Now.AddHours(5), 30, ["完成"]));

        Assert.Empty(service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items);
        // 草稿对所有者可见，对其他人按不存在处理。
        Assert.NotNull(service.GetPublic(draft.Id, owner));
        Assert.Null(service.GetPublic(draft.Id, Guid.NewGuid()));
        Assert.Null(service.GetPublic(draft.Id, null));
    }

    [Fact]
    public void Published_task_detail_is_public()
    {
        var (service, owner) = CreateService();
        var taskId = Publish(service, owner, "已发布任务", "浦东新区", 30, Now.AddHours(2));

        Assert.NotNull(service.GetPublic(taskId, null));
        Assert.NotNull(service.GetPublic(taskId, Guid.NewGuid()));
    }

    [Fact]
    public void Invalid_filters_and_cursors_are_rejected()
    {
        var (service, _) = CreateService();

        Assert.Throws<ArgumentException>(() => service.SearchPublished(new TaskSearchRequest(null, 60, 40, 10, null)));
        Assert.Throws<ArgumentException>(() => service.SearchPublished(new TaskSearchRequest(null, -1, null, 10, null)));
        var error = Assert.Throws<ArgumentException>(() => service.SearchPublished(new TaskSearchRequest(null, null, null, 10, "不是一个游标")));
        Assert.Equal("分页游标无效，请重新加载列表。", error.Message);
    }

    [Fact]
    public void Execution_address_is_staged_for_owner_and_selected_worker_only()
    {
        var (service, owner) = CreateService();
        var selectedWorker = Guid.NewGuid();
        var otherWorker = Guid.NewGuid();
        var task = service.Create(new CreateTaskRequest(owner, "代取文件", "到前台取一份普通文件", "浦东新区", Now.AddHours(6), 50, ["上传取件码照片"], "世纪大道 100 号前台"));
        service.Publish(task.Id, owner);
        var selected = service.Apply(task.Id, new ApplyForTaskRequest(selectedWorker, "半小时可到"));
        service.Apply(task.Id, new ApplyForTaskRequest(otherWorker, "一小时可到"));

        // 未选人前只有所有者能读。
        Assert.Equal("世纪大道 100 号前台", service.GetExecutionAddress(task.Id, owner));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetExecutionAddress(task.Id, selectedWorker));

        service.Select(task.Id, selected.Applications.Single().Id, new SelectApplicationRequest(owner));

        Assert.Equal("世纪大道 100 号前台", service.GetExecutionAddress(task.Id, owner));
        Assert.Equal("世纪大道 100 号前台", service.GetExecutionAddress(task.Id, selectedWorker));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetExecutionAddress(task.Id, otherWorker));
        Assert.Throws<UnauthorizedAccessException>(() => service.GetExecutionAddress(task.Id, Guid.NewGuid()));
    }

    [Fact]
    public void Hall_list_and_public_detail_never_expose_the_address()
    {
        var (service, owner) = CreateService();
        var task = service.Create(new CreateTaskRequest(owner, "代取文件", "到前台取一份普通文件", "浦东新区", Now.AddHours(6), 50, ["完成"], "世纪大道 100 号前台"));
        service.Publish(task.Id, owner);

        var summary = service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items.Single();

        // 公开信息里只暴露“是否填写了执行地址”，地址本身要走受控接口。
        Assert.True(summary.HasExecutionAddress);
        Assert.DoesNotContain("世纪大道", System.Text.Json.JsonSerializer.Serialize(summary), StringComparison.Ordinal);
        Assert.DoesNotContain("世纪大道", System.Text.Json.JsonSerializer.Serialize(service.GetPublic(task.Id, null)), StringComparison.Ordinal);
    }

    [Fact]
    public void Task_without_address_returns_null_instead_of_forbidden()
    {
        var (service, owner) = CreateService();
        var taskId = Publish(service, owner, "没有地址", "浦东新区", 30, Now.AddHours(2));

        Assert.Null(service.GetExecutionAddress(taskId, owner));
        Assert.Null(service.GetExecutionAddress(taskId, Guid.NewGuid()));
        Assert.False(service.GetPublic(taskId, null)!.HasExecutionAddress);
    }

    private static Guid Publish(TaskService service, Guid owner, string title, string district, decimal reward, DateTimeOffset deadline)
    {
        var task = service.Create(new CreateTaskRequest(owner, title, $"{title} 的描述", district, deadline, reward, ["完成"]));
        service.Publish(task.Id, owner);
        return task.Id;
    }

    private static (TaskService Service, Guid Owner) CreateService()
    {
        var clock = new FixedTimeProvider(Now);
        var service = new TaskService(
            new InMemoryTaskRepository(),
            new InMemoryOrderRepository(),
            new InMemoryReviewRepository(),
            clock,
            new NotificationService(new InMemoryNotificationRepository(), clock),
            new InMemoryUnitOfWork());
        return (service, Guid.NewGuid());
    }
}
