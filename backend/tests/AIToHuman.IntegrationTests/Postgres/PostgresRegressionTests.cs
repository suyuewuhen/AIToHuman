using AIToHuman.Application.Idempotency;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Admin;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Idempotency;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Risk;
using AIToHuman.Domain.Tasks;
using Microsoft.AspNetCore.Http;
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

    /// <summary>
    /// 草稿版本历史在真库上的完整性：编号单调、每次编辑一版，
    /// 并且**并发落败的那次编辑不能留下多余版本**（版本快照与任务写入在同一个事务里）。
    /// </summary>
    [PostgresFact]
    public async Task Draft_revisions_are_numbered_in_order_and_a_losing_edit_leaves_no_trace()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreateDraft("明天下午帮我去前台取一份文件");

        world.Service.UpdateDraft(taskId, world.Owner, world.DraftUpdateRequest("明天下午帮我去公司前台取一份文件", reward: 60));

        // 两个作用域各读到同一版本；先写成功，后写必须冲突。
        using var staleScope = world.NewScope();
        var staleService = world.ServiceIn(staleScope);
        staleService.Get(taskId);
        world.Service.UpdateDraft(taskId, world.Owner, world.DraftUpdateRequest("明天下午帮我去公司前台取一份文件", district: "西城区"));
        Assert.Throws<DbUpdateConcurrencyException>(() =>
            staleService.UpdateDraft(taskId, world.Owner, world.DraftUpdateRequest("这次改动不该留下痕迹", reward: 99)));

        await using var context = fixture.CreateContext();
        var revisions = await context.TaskRevisions.AsNoTracking()
            .Where(item => item.TaskId == taskId)
            .OrderBy(item => item.Revision)
            .ToListAsync();

        Assert.Equal([1, 2, 3], revisions.Select(item => item.Revision));
        Assert.Equal("创建草稿", revisions[0].ChangeSummary);
        Assert.DoesNotContain(revisions, item => item.Title == "这次改动不该留下痕迹");
        // 唯一索引 (TaskId, Revision) 也在这条链路上被真实写入验证过。
        Assert.Equal(3, revisions.Count);
    }

    /// <summary>
    /// 幂等记录在真库上的行为：主键是 (UserId, Key)，跨作用域（等价于跨请求/跨实例）能读回并回放，
    /// 同一用户同一键只能占一次，不同用户可以用相同的键。
    /// </summary>
    [PostgresFact]
    public async Task Idempotency_records_are_shared_across_scopes_and_keyed_per_user()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var user = Guid.NewGuid();
        var other = Guid.NewGuid();

        using (var scope = world.NewScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            var entry = IdempotencyEntry.Start(user, "retry-1", new string('a', 64), Now);
            Assert.True(store.TryStart(entry, out _));
            store.Complete(entry, StatusCodes.Status201Created, """{"id":"task-1"}""", "application/json; charset=utf-8", Now);
        }

        using (var freshScope = world.NewScope())
        {
            var store = freshScope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            var found = store.Find(user, "retry-1");

            Assert.NotNull(found);
            Assert.True(found!.IsCompleted);
            Assert.Equal(StatusCodes.Status201Created, found.StatusCode);
            Assert.Equal("""{"id":"task-1"}""", found.ResponseBody);

            // 同一个用户再占一次会被挡住，并交回已有记录。
            Assert.False(store.TryStart(IdempotencyEntry.Start(user, "retry-1", new string('a', 64), Now), out var existing));
            Assert.Equal(StatusCodes.Status201Created, existing!.StatusCode);

            // 键是按用户隔离的：另一个用户用同样的键不受影响。
            Assert.True(store.TryStart(IdempotencyEntry.Start(other, "retry-1", new string('b', 64), Now), out _));
        }
    }

    /// <summary>
    /// 误拦申诉在真库上的落库与安全边界：申诉与处置结论都要持久化，
    /// 并且**禁止类别即使申诉成立也不能发布**——这条红线不能因为多了一个入口就被绕过。
    /// </summary>
    [PostgresFact]
    public async Task Risk_appeals_persist_and_cannot_release_a_prohibited_task()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);

        // 转人工后被驳回 → 申诉成立 → 放行。
        var rejected = world.CreateDraft("帮我把营业执照原件送到银行");
        world.Admin.DecideRiskReview(rejected, "Reject", "无法核实执照用途", world.AdminId);
        world.Service.OpenRiskAppeal(rejected, world.Owner, "是我自己公司的执照");
        world.Appeals.DecideAppeal(rejected, accepted: true, "已核对授权说明，属于误判", world.AdminId);

        // 禁止类别 → 申诉成立也不放行。
        var blocked = world.CreateDraft("帮我代考英语四级");
        world.Service.OpenRiskAppeal(blocked, world.Owner, "标题是引用别人的例子");
        world.Appeals.DecideAppeal(blocked, accepted: true, "确认规则误伤，已记录用于改进词表", world.AdminId);

        await using (var context = fixture.CreateContext())
        {
            var rejectedRecord = await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == rejected);
            Assert.Equal("Accepted", rejectedRecord.RiskAppealStatus);
            Assert.Equal("是我自己公司的执照", rejectedRecord.RiskAppealReason);
            Assert.Equal("Approved", rejectedRecord.RiskReviewStatus);
            Assert.Equal(world.AdminId, rejectedRecord.RiskAppealDecidedBy);

            var blockedRecord = await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == blocked);
            Assert.Equal("Accepted", blockedRecord.RiskAppealStatus);
            Assert.Equal("Blocked", blockedRecord.RiskVerdict);
        }

        using (var freshScope = world.NewScope())
        {
            var service = world.ServiceIn(freshScope);
            Assert.Equal(TaskStatus.Published.ToString(), service.Publish(rejected, world.Owner).Status);
            // 禁止类别：申诉结论是"误伤"，但发布这一关依旧过不去。
            var error = Assert.Throws<DomainException>(() => service.Publish(blocked, world.Owner));
            Assert.Contains("平台禁止的类别", error.Message);
        }

        await using var auditContext = fixture.CreateContext();
        var audits = await auditContext.AdminAudits.AsNoTracking().Where(item => item.Action.StartsWith("task.risk.appeal")).ToListAsync();
        Assert.Equal(2, audits.Count);
    }

    /// <summary>
    /// 幂等记录的清理在真库上要走一条 DELETE（而不是把记录读进内存）：只删过期的，
    /// 还在重试窗口内的必须留下，否则"重试去重"会在最需要的时候失效。
    /// </summary>
    [PostgresFact]
    public async Task Expired_idempotency_records_are_deleted_without_touching_fresh_ones()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var user = Guid.NewGuid();

        using (var scope = world.NewScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            var expired = IdempotencyEntry.Start(user, "expired", new string('a', 64), Now - TimeSpan.FromDays(2));
            store.TryStart(expired, out _);
            store.Complete(expired, StatusCodes.Status201Created, """{"id":"old"}""", "application/json", Now - TimeSpan.FromDays(2));

            var fresh = IdempotencyEntry.Start(user, "fresh", new string('b', 64), Now - TimeSpan.FromMinutes(1));
            store.TryStart(fresh, out _);
            store.Complete(fresh, StatusCodes.Status201Created, """{"id":"new"}""", "application/json", Now);
        }

        using (var scope = world.NewScope())
        {
            var cleanup = new IdempotencyCleanup(scope.ServiceProvider.GetRequiredService<IIdempotencyStore>(), world.Clock);
            var result = cleanup.Sweep();
            Assert.Equal(1, result.Deleted);
        }

        await using var context = fixture.CreateContext();
        var rows = await context.IdempotencyEntries.AsNoTracking().ToListAsync();
        Assert.Equal("fresh", Assert.Single(rows).Key);
    }

    /// <summary>
    /// 草稿回滚在真库上的落库：字段写回 + **追加**一版（不覆盖中间版本），
    /// 并且回滚后的风险结论与人工复核状态也要跟着重置。
    /// </summary>
    [PostgresFact]
    public async Task Restoring_a_revision_persists_and_appends_a_new_version()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreateDraft("明天下午帮我去前台取一份文件");
        world.Service.UpdateDraft(taskId, world.Owner, world.DraftUpdateRequest("改成去菜市场买两斤苹果", district: "西城区", reward: 60));

        var restored = world.Service.RestoreDraftRevision(taskId, revision: 1, world.Owner);

        Assert.Equal("明天下午帮我去前台取一份文件", restored.Title);
        Assert.Equal("朝阳区", restored.District);
        Assert.Equal(50m, restored.Reward);

        // 换一个作用域读回来：确认落库的是回滚后的内容，而不是内存里的副本。
        using (var freshScope = world.NewScope())
        {
            var reloaded = world.ServiceIn(freshScope).Get(taskId)!;
            Assert.Equal("明天下午帮我去前台取一份文件", reloaded.Title);
            Assert.Equal("朝阳区", reloaded.District);
        }

        await using var context = fixture.CreateContext();
        var revisions = await context.TaskRevisions.AsNoTracking()
            .Where(item => item.TaskId == taskId)
            .OrderBy(item => item.Revision)
            .ToListAsync();

        // 三版：创建、编辑、回滚。中间那版没有被覆盖。
        Assert.Equal([1, 2, 3], revisions.Select(item => item.Revision));
        Assert.Equal("改成去菜市场买两斤苹果", revisions[1].Title);
        Assert.StartsWith("回滚自第 1 版", revisions[2].ChangeSummary);
        Assert.Equal("明天下午帮我去前台取一份文件", revisions[2].Title);
    }

    /// <summary>
    /// 风险规则目录在真库上的往返：只追加、版本号唯一、读取取最新一版，
    /// 而且**新版本立刻对后续判定生效**（任务上记的规则版本号跟着变，老任务仍然停在它当时的版本）。
    /// 这类"目录改了但判定还按老规则"的缺陷内存替身同样测不出来——两边共用的都是同一个对象。
    /// </summary>
    [PostgresFact]
    public async Task A_new_rule_catalog_version_is_persisted_and_applies_to_later_tasks()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);

        // 改规则之前先建一条草稿：它的判定应当停在 v1，不会被后来的版本追溯改写。
        var earlyTaskId = world.CreateDraft("明天下午帮我去前台取一份文件");

        var before = world.RiskRules.GetDetail();
        Assert.True(before.IsBuiltIn);
        Assert.Equal(RiskRuleCatalog.BuiltInVersion, before.Version);

        var updated = Update(world, before, "新增一类禁止任务：有偿代占考试座位", 8000m,
            new RiskRuleDetailRequest("prohibited.exam_seat_reservation", "有偿代占考试座位", "Blocked",
                "任务涉及有偿代抢或代占考试座位，属于平台禁止的考试作弊协助。", ["代抢座位", "代占座位"]));

        Assert.Equal(2, updated.Version);
        Assert.False(updated.IsBuiltIn);
        Assert.Equal(world.AdminId, updated.UpdatedBy);
        Assert.Equal("新增一类禁止任务：有偿代占考试座位", updated.ChangeReason);
        Assert.Contains("新增规则 1 条", updated.ChangeSummary);
        Assert.Contains("高金额阈值 5000 → 8000 元", updated.ChangeSummary);

        // 换一个作用域（另一个请求）读：确认新版本真的落库了，而不是只活在当前上下文里。
        using (var freshScope = world.NewScope())
        {
            var reloaded = world.RiskRulesIn(freshScope).GetDetail();
            Assert.Equal(2, reloaded.Version);
            Assert.Contains(reloaded.Rules, rule => rule.Code == "prohibited.exam_seat_reservation" && rule.Keywords.Contains("代占座位"));
        }

        // 新规则对后续写入立刻生效：命中新规则的草稿被拦，且记的是 v2。
        var blocked = world.Service.Get(world.CreateDraft("帮我去学校代占座位"))!;
        Assert.Equal("Blocked", blocked.RiskVerdict);
        Assert.Equal("prohibited.exam_seat_reservation", blocked.RiskRuleCode);
        Assert.Equal(2, blocked.RiskRuleVersion);

        // 阈值也换成了新值：6000 元在 v1 下会转人工，阈值抬到 8000 之后不再转（但记录里写的是 v2），
        // 9000 元仍然转人工。
        var midPrice = world.Service.Get(world.CreateDraft("帮我取一份文件", reward: 6000))!;
        Assert.Equal("Allowed", midPrice.RiskVerdict);
        Assert.Equal(2, midPrice.RiskRuleVersion);

        var pricey = world.Service.Get(world.CreateDraft("帮我取一份文件", reward: 9000))!;
        Assert.Equal("NeedsReview", pricey.RiskVerdict);
        Assert.Equal(RiskRuleCatalog.HighRewardRuleCode, pricey.RiskRuleCode);
        Assert.Equal(2, pricey.RiskRuleVersion);

        // 早先那条草稿的判定留在 v1：规则升级不该改写历史结论。
        Assert.Equal(RiskRuleCatalog.BuiltInVersion, world.Service.Get(earlyTaskId)!.RiskRuleVersion);

        await using var context = fixture.CreateContext();
        var rows = await context.RiskRuleCatalogRevisions.AsNoTracking().ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(2, row.Version);
        Assert.Equal(world.AdminId, row.UpdatedBy);
        Assert.Contains("prohibited.exam_seat_reservation", row.CatalogJson);

        // 版本号是唯一索引：再怎么并发写也不该出现两行 v2。
        using (var scope = world.NewScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IRiskRuleCatalogStore>();
            var duplicate = RiskRuleCatalogRevision.Rehydrate(
                Guid.NewGuid(), row.CatalogJson, row.ChangeSummary, row.ChangeReason, row.UpdatedBy, Now);
            Assert.Throws<DbUpdateException>(() => store.Append(duplicate));
        }
    }

    /// <summary>
    /// 发布这一关用的是"发布那一刻生效的规则"：草稿创建之后运营收紧了词表，同一条草稿就发不出去了。
    /// 这正是"改规则要能真的拦住"的端到端证据（而不是只在后台看到版本号变了）。
    /// </summary>
    [PostgresFact]
    public void Tightening_the_rules_after_a_draft_exists_blocks_publishing_it()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreateDraft("帮我去实验楼取一份材料");
        Assert.Equal("Allowed", world.Service.Get(taskId)!.RiskVerdict);

        Update(world, world.RiskRules.GetDetail(), "把实验楼列入禁止进入的场所", 5000m,
            new RiskRuleDetailRequest("prohibited.lab_break_in", "擅自进入实验场所", "Blocked",
                "任务涉及未经许可进入实验室等受控场所。", ["实验楼", "实验室"]));

        var exception = Assert.Throws<DomainException>(() => world.Service.Publish(taskId, world.Owner));
        Assert.Contains("平台禁止的类别", exception.Message);

        // 换一个作用域从库里读回来：这次发布整体没有产生任何写入，任务仍是草稿。
        using (var freshScope = world.NewScope())
        {
            var reloaded = world.ServiceIn(freshScope).Get(taskId)!;
            Assert.Equal("ReadyToPublish", reloaded.Status);
        }
    }

    /// <summary>用给定的规则清单（内置目录 + 一条新规则）替换整份目录，省掉每个用例重复抄模板。</summary>
    private static RiskRuleCatalogDetailResponse Update(
        PostgresWorld world,
        RiskRuleCatalogDetailResponse current,
        string reason,
        decimal highRewardThreshold,
        RiskRuleDetailRequest extraRule) =>
        world.RiskRules.Update(
            new UpdateRiskRuleCatalogRequest(
                current.Version,
                reason,
                highRewardThreshold,
                current.NightWindowStart,
                current.NightWindowEnd,
                [
                    .. current.Rules.Select(rule => new RiskRuleDetailRequest(rule.Code, rule.Category, rule.Verdict, rule.Description, rule.Keywords)),
                    extraRule
                ]),
            world.AdminId);

    /// <summary>
    /// 发布后风控处置在真库上的往返：规则升级后复检把一条在线任务自动下架，
    /// 新增的三列（处置状态、原因、时刻）必须真的落库、并能被另一个作用域读回来
    /// ——这一层正是"手写列复制漏列"最容易出错的地方（见本文件开头那条草稿编辑缺陷）。
    /// </summary>
    [PostgresFact]
    public async Task Risk_enforcement_round_trips_and_the_sweep_is_idempotent()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreatePublished("帮我把一台无人机送到郊区");
        var applicationId = world.Apply(taskId);

        // 运营收紧规则：把"无人机"列为禁止类别。
        Update(world, world.RiskRules.GetDetail(), "把无人机作业列为禁止类别", 5000m,
            new RiskRuleDetailRequest("prohibited.no_drones", "未经许可的无人机作业", "Blocked",
                "任务涉及未经许可的无人机飞行，属于受管制活动。", ["无人机", "穿越机"]));

        var result = world.Enforcement.Recheck();
        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Unpublished);

        // 换一个作用域（另一个请求）读回来：确认新列真的落库了，而不是只活在当前上下文里。
        using (var freshScope = world.NewScope())
        {
            var reloaded = world.ServiceIn(freshScope).Get(taskId)!;
            Assert.Equal("Cancelled", reloaded.Status);
            Assert.Equal("Suspended", reloaded.RiskEnforcementStatus);
            Assert.Contains("prohibited.no_drones", reloaded.RiskEnforcementReason);
            Assert.NotNull(reloaded.RiskEnforcedAt);
        }

        await using var context = fixture.CreateContext();
        var record = await context.Tasks.AsNoTracking().SingleAsync(item => item.Id == taskId);
        Assert.Equal("Cancelled", record.Status);
        Assert.Equal("Suspended", record.RiskEnforcementStatus);
        Assert.Contains("无人机", record.RiskEnforcementReason);
        Assert.NotNull(record.RiskEnforcedAt);
        Assert.Contains("prohibited.no_drones", record.CancellationReason);

        // 平台的处置在运营审计里留痕（操作人是系统身份），被作废报名的服务者收到提醒。
        var audit = await context.AdminAudits.AsNoTracking().SingleAsync(item => item.Action == RiskEnforcementService.UnpublishAction);
        Assert.Equal(AdminAuditEntry.SystemActorId, audit.ActorId);
        Assert.Equal(taskId, audit.TargetId);

        var notifications = await context.Notifications.AsNoTracking()
            .Where(item => item.UserId == world.Worker)
            .Select(item => item.Type)
            .ToListAsync();
        Assert.Contains("task.cancelled", notifications);
        Assert.NotEqual(Guid.Empty, applicationId);

        // 第二轮不会再扫到它：版本号已经刷成最新，这也是"重复处置/重复通知"的天然屏障。
        Assert.Equal(0, world.Enforcement.Recheck().Scanned);
    }

    /// <summary>
    /// 申诉留档在真库上的往返：一次申诉一行、扣上提交时的规则与版本，结论写回同一行；
    /// 节流用的两个计数在数据库上算（COUNT），换一个作用域读回来仍然正确。
    /// </summary>
    [PostgresFact]
    public async Task Appeal_records_round_trip_and_the_throttle_counts_come_from_the_database()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var taskId = world.CreateDraft("帮我代考英语四级");

        world.Service.OpenRiskAppeal(taskId, world.Owner, "误判：只是给家里人帮忙");

        using (var freshScope = world.NewScope())
        {
            var records = freshScope.ServiceProvider.GetRequiredService<IRiskAppealRepository>();
            var record = Assert.Single(records.ListByTask(taskId));
            Assert.Equal("prohibited.exam_impersonation", record.RuleCode);
            Assert.Equal(RiskRuleCatalog.BuiltIn.Version, record.RuleVersion);
            Assert.Equal(RiskAppealStatus.Pending, record.Status);
            // 窗口内算一次，窗口外不算：这两条正是节流查询的语义。
            Assert.Equal(1, records.CountByOwnerSince(world.Owner, Now - RiskAppealPolicy.Window));
            Assert.Equal(0, records.CountByOwnerSince(world.Owner, Now + RiskAppealPolicy.Window));
            Assert.Equal(1, records.CountByTasks([taskId, Guid.NewGuid()]).GetValueOrDefault(taskId));
        }

        world.Appeals.DecideAppeal(taskId, accepted: false, "维持原判：标题写的就是代考", world.AdminId);

        await using var context = fixture.CreateContext();
        var row = await context.RiskAppeals.AsNoTracking().SingleAsync();
        Assert.Equal(taskId, row.TaskId);
        Assert.Equal(world.Owner, row.OwnerId);
        Assert.Equal("Denied", row.Status);
        Assert.Equal(world.AdminId, row.DecidedBy);
        Assert.NotNull(row.DecidedAt);
        Assert.Equal("维持原判：标题写的就是代考", row.DecisionNote);
    }

    /// <summary>
    /// 精确地址访问留痕在真库上的往返：披露与被拒绝的读取都要落库，换一个作用域读回来仍然正确，
    /// 且按任务的拒绝次数能被算出来（运营靠它发现有人在试探）。
    /// </summary>
    [PostgresFact]
    public async Task Address_access_entries_round_trip_including_denied_attempts()
    {
        using var world = new PostgresWorld(fixture.ConnectionString, Now);
        var task = world.Service.Create(world.DraftRequest("代取文件", executionAddress: "世纪大道 100 号前台"));
        world.Service.Publish(task.Id, world.Owner);
        var taskId = task.Id;
        var stranger = Guid.NewGuid();

        Assert.NotNull(world.AddressAccess.Read(taskId, world.Owner).Address);
        Assert.Throws<UnauthorizedAccessException>(() => world.AddressAccess.Read(taskId, stranger));

        using (var freshScope = world.NewScope())
        {
            var records = freshScope.ServiceProvider.GetRequiredService<IAddressAccessRepository>();
            var entries = records.List(taskId, null, 20);
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, item => item.Role == AddressAccessRole.Owner && item.Outcome == AddressAccessOutcome.Granted);
            Assert.Contains(entries, item => item.ViewerId == stranger && item.Outcome == AddressAccessOutcome.Denied);
            Assert.Equal(1, records.CountDenied(taskId, null));
            Assert.Single(records.List(null, stranger, 20));
        }

        await using var context = fixture.CreateContext();
        var rows = await context.AddressAccessEntries.AsNoTracking().OrderBy(item => item.OccurredAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, item => item.Outcome == "Granted" && item.ViewerRole == "Owner" && item.ViewerId == world.Owner);
        Assert.Contains(rows, item => item.Outcome == "Denied" && item.ViewerRole == "Other" && item.ViewerId == stranger);
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
