using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确指向领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 只跑在真实 PostgreSQL 上的回归用例。这里放的每一条都对应一种**内存替身测不出来**的缺陷：
/// 并发写入、EF 变更跟踪、迁移与真实 SQL 语义。数据库不可用时整体跳过（见 <see cref="PostgresFactAttribute"/>）。
/// </summary>
[Collection(PostgresRegressionCollection.Name)]
public sealed class PostgresRegressionTests(PostgresRegressionFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [PostgresFact]
    public async Task A_fresh_database_gets_every_migration_and_nothing_is_left_pending()
    {
        var (applied, pending) = await fixture.MigrationStateAsync();

        // 库是 fixture 用 Migrate() 从零建出来的：应用数必须等于模型里的迁移总数，且没有待执行。
        Assert.True(applied >= 20, $"迁移应从零全量应用，实际只应用了 {applied} 个");
        Assert.Equal(0, pending);
    }

    /// <summary>
    /// 草稿编辑落库：曾经 <c>EfTaskRepository.Save</c> 逐列手写复制，漏掉了草稿正文那几列，
    /// 表现为“接口返回新内容、库里还是旧的、发布时按旧文本判风险”。内存仓储保存的是同一个对象引用，
    /// 所以只有真库能发现。这里用**另一个作用域**重新读，确保读到的是数据库里的值。
    /// </summary>
    [PostgresFact]
    public async Task Editing_a_draft_persists_every_column_not_just_reward_and_status()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreateDraft("明天下午帮我去前台取一份文件");

        var newDeadline = Now.AddHours(30);
        world.Service.UpdateDraft(taskId, world.Owner, new UpdateTaskDraftRequest(
            world.Owner, "改成去菜市场买两斤苹果送到家", "买完送到家门口，路上别压坏", "西城区",
            newDeadline, 66, ["苹果完好", "送到家门口"], "西城区某某路 2 号", Now.AddHours(2)));

        using var freshScope = world.NewScope();
        var reloaded = world.ServiceIn(freshScope).Get(taskId)!;

        Assert.Equal("改成去菜市场买两斤苹果送到家", reloaded.Title);
        Assert.Equal("买完送到家门口，路上别压坏", reloaded.Description);
        Assert.Equal("西城区", reloaded.District);
        Assert.Equal(newDeadline, reloaded.Deadline);
        Assert.Equal(66m, reloaded.Reward);
        Assert.Equal(["苹果完好", "送到家门口"], reloaded.AcceptanceCriteria);
        Assert.Equal(Now.AddHours(2), reloaded.ApplicationDeadline);

        // 执行地址只走参与者层接口，这里用领域读取确认它也确实落库了。
        await using var context = fixture.CreateContext();
        var record = await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        Assert.Equal("西城区某某路 2 号", record.ExecutionAddress);
    }

    /// <summary>
    /// 乐观并发令牌必须真的生效：两个作用域各自读到同一版本，后写入者要拿到 DbUpdateConcurrencyException。
    /// 这条正是当初“令牌形同虚设”（Get 用了 AsNoTracking、Save 又按当前库值自增）漏掉的那一类。
    /// </summary>
    [PostgresFact]
    public void A_stale_write_is_rejected_by_the_concurrency_token()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreateDraft("帮我去营业厅拍一张营业时间照片");

        using var firstScope = world.NewScope();
        using var secondScope = world.NewScope();
        var first = world.ServiceIn(firstScope);
        var second = world.ServiceIn(secondScope);

        var firstCopy = first.Get(taskId)!;
        var secondCopy = second.Get(taskId)!;
        Assert.Equal(firstCopy.Title, secondCopy.Title);

        first.UpdateDraft(taskId, world.Owner, world.DraftUpdateRequest("第一个请求改成去图书馆还两本书"));

        // 第二个请求手里还是旧版本：保存时必须冲突，而不是默默覆盖。
        var error = Assert.Throws<DbUpdateConcurrencyException>(() =>
            second.UpdateDraft(taskId, world.Owner, world.DraftUpdateRequest("第二个请求改成去菜市场买苹果")));
        Assert.NotNull(error);
    }

    /// <summary>12 路并发选人只允许产生一个订单——真实事务 + 唯一索引 + 版本令牌协同的结果。</summary>
    [PostgresFact]
    public async Task Twelve_parallel_selections_produce_exactly_one_order()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreatePublished("帮我去菜市场买两斤苹果");

        // 12 个服务者先各自报名（每个请求一个作用域，模拟并发连接）。
        var applications = new List<Guid>();
        for (var index = 0; index < 12; index++)
        {
            using var scope = world.NewScope();
            var service = world.ServiceIn(scope);
            var workerId = Guid.NewGuid();
            var applied = service.Apply(taskId, new ApplyForTaskRequest(workerId, $"第 {index + 1} 位"));
            applications.Add(applied.Applications.Single(item => item.WorkerId == workerId).Id);
        }

        var results = await Task.WhenAll(applications.Select(async applicationId =>
        {
            using var scope = world.NewScope();
            var service = world.ServiceIn(scope);
            try
            {
                await Task.Yield();
                service.Select(taskId, applicationId, new SelectApplicationRequest(world.Owner));
                return "ok";
            }
            catch (Exception exception) when (exception is DomainException or DbUpdateConcurrencyException or InvalidOperationException)
            {
                return "rejected";
            }
        }));

        Assert.Equal(1, results.Count(result => result == "ok"));

        await using var context = fixture.CreateContext();
        Assert.Equal(1, await context.Orders.CountAsync(item => item.TaskId == taskId));
        Assert.Equal(1, await context.Applications.CountAsync(item => item.TaskId == taskId && item.Status == "Selected"));
        Assert.Equal(TaskStatus.Assigned.ToString(), (await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == taskId)).Status);
    }

    /// <summary>取消订单的连带效果在真库上要落到 tasks / task_applications / orders 三张表里。</summary>
    [PostgresFact]
    public async Task Cancelling_an_order_puts_the_task_back_into_the_hall()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreatePublished("帮我去图书馆还两本书");
        var applicationId = world.Apply(taskId);
        var orderId = world.Service.Select(taskId, applicationId, new SelectApplicationRequest(world.Owner)).Order.Id;

        world.Service.CancelOrder(orderId, world.Owner, "临时不需要了");

        await using var context = fixture.CreateContext();
        var task = await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        Assert.Equal(TaskStatus.Published.ToString(), task.Status);
        Assert.Equal("Rejected", (await context.Applications.AsNoTracking().SingleAsync(item => item.Id == applicationId)).Status);
        var order = await context.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        Assert.Equal(OrderStatus.Cancelled.ToString(), order.Status);
        Assert.Equal(world.Owner, order.CancelledBy);
        Assert.Equal("临时不需要了", order.CancellationReason);
        // 任务回到大厅：不能再报名前先确认它在公开列表里。
        Assert.Contains(world.Service.SearchPublished(new TaskSearchRequest(null, null, null, 10, null)).Items, item => item.Id == taskId);
    }

    /// <summary>争议一旦发起，订单在真库上要冻结：状态、时间与发起人都落库，双方动作全部被领域规则拒绝。</summary>
    [PostgresFact]
    public async Task A_disputed_order_is_frozen_in_the_database()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreatePublished("帮我去营业厅拍一张营业时间照片");
        var applicationId = world.Apply(taskId);
        var orderId = world.Service.Select(taskId, applicationId, new SelectApplicationRequest(world.Owner)).Order.Id;
        world.Service.StartOrder(orderId, world.Worker);
        world.Service.SubmitOrder(orderId, world.Worker, "拍好了");

        world.Service.OpenDispute(orderId, world.Owner, "照片看不清营业时间");

        await using var context = fixture.CreateContext();
        var order = await context.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        Assert.Equal(OrderStatus.Disputed.ToString(), order.Status);
        Assert.Equal(world.Owner, order.DisputeOpenedBy);
        Assert.NotNull(order.DisputeOpenedAt);
        Assert.Equal("照片看不清营业时间", order.DisputeReason);

        Assert.Throws<DomainException>(() => world.Service.ApproveOrder(orderId, world.Owner, "算了吧"));
        Assert.Throws<DomainException>(() => world.Service.SubmitOrder(orderId, world.Worker, "再传一次"));
    }

    /// <summary>风险复核队列在真库上按状态过滤：放行与编辑后重新排队都要如实反映到查询里。</summary>
    [PostgresFact]
    public async Task The_risk_review_queue_round_trips_through_postgres()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var approved = world.CreateDraft("帮我把营业执照原件送到银行");
        var blocked = world.CreateDraft("帮我代考英语四级");

        // 只有“转人工”的任务在队列里；禁止类别不进队列。
        var queue = world.Admin.ListRiskReviews(20).Items;
        var queued = Assert.Single(queue);
        Assert.Equal(approved, queued.TaskId);
        Assert.DoesNotContain(blocked, queue.Select(item => item.TaskId));

        world.Admin.DecideRiskReview(approved, "Approve", "已核实执照用途", world.AdminId);
        Assert.Empty(world.Admin.ListRiskReviews(20).Items);

        // 编辑会让复核作废并重新排队——这条防绕过规则同样要经得起真库。
        world.Service.UpdateDraft(approved, world.Owner, world.DraftUpdateRequest("帮我把身份证和户口本原件送到银行"));
        Assert.Equal(approved, Assert.Single(world.Admin.ListRiskReviews(20).Items).TaskId);
        Assert.Throws<DomainException>(() => world.Service.Publish(approved, world.Owner));

        world.Admin.DecideRiskReview(approved, "Approve", "复核新内容后放行", world.AdminId);
        Assert.Equal(TaskStatus.Published.ToString(), world.Service.Publish(approved, world.Owner).Status);
    }

    /// <summary>带时区偏移的截止时间与中文文本在真库上的往返：领域层统一归一化成 UTC，文本原样保存。</summary>
    [PostgresFact]
    public async Task Utc_offsets_and_chinese_text_round_trip_through_the_database()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var deadline = new DateTimeOffset(2026, 9, 15, 15, 30, 0, TimeSpan.FromHours(8));
        var id = world.Service.Create(world.DraftRequest("帮我把一份合同复印件送到公司前台", "送到前台交给行政即可", "朝阳区", deadline, 80, ["当面交给行政"])).Id;

        await using var context = fixture.CreateContext();
        var record = await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == id);

        Assert.Equal(deadline.ToUniversalTime(), record.Deadline);
        Assert.Equal(TimeSpan.Zero, record.Deadline.Offset);
        Assert.Equal("帮我把一份合同复印件送到公司前台", record.Title);
        Assert.Equal("朝阳区", record.District);
    }
}
