using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Risk;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Risk;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 申诉节流与留档的用例层行为：单条任务与单日都设上限（挡住"改一个字再申诉"的刷屏），
/// 每一次申诉都留档（包含运营结论），运营在界面上能看到"这条任务被申诉过几次"。
/// </summary>
public sealed class RiskAppealThrottleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void One_task_cannot_be_appealed_more_than_the_cap()
    {
        var world = new World();
        var taskId = world.CreateBlocked("帮我代考英语四级");

        // 每次申诉都被驳回后改文案（改文案会清掉申诉状态，于是可以再申诉一次）。
        for (var attempt = 1; attempt <= RiskAppealPolicy.MaxPerTask; attempt++)
        {
            var appealed = world.TasksService.OpenRiskAppeal(taskId, world.Owner, $"第 {attempt} 次：这真的只是普通跑腿");
            Assert.Equal("Pending", appealed.RiskAppealStatus);
            world.Appeals.DecideAppeal(taskId, accepted: false, "维持原判", world.AdminId);

            if (attempt < RiskAppealPolicy.MaxPerTask)
            {
                // 改文案会清掉申诉状态，于是可以再申诉一次；文案仍然命中禁止类别，所以仍然具备申诉资格。
                world.TasksService.UpdateDraft(taskId, world.Owner, world.DraftUpdate($"帮我代考英语四级 第 {attempt + 1} 次"));
            }
        }

        Assert.Equal(RiskAppealPolicy.MaxPerTask, world.AppealRepository.CountByTask(taskId));

        var error = Assert.Throws<DomainException>(() => world.TasksService.OpenRiskAppeal(taskId, world.Owner, "第四次"));
        Assert.Contains("累计申诉已达上限", error.Message);
        // 被挡住的那次不该留下记录。
        Assert.Equal(RiskAppealPolicy.MaxPerTask, world.AppealRepository.CountByTask(taskId));
    }

    [Fact]
    public void One_person_cannot_flood_the_queue_with_many_tasks()
    {
        var world = new World();
        var taskIds = Enumerable.Range(0, RiskAppealPolicy.MaxPerOwnerPerDay + 1)
            .Select(index => world.CreateBlocked($"帮我代考英语四级 {index}"))
            .ToArray();

        // 前 5 条都能申诉（每条都远没到单任务上限）。
        foreach (var taskId in taskIds.Take(RiskAppealPolicy.MaxPerOwnerPerDay))
        {
            world.TasksService.OpenRiskAppeal(taskId, world.Owner, "误判，请复核");
        }

        var error = Assert.Throws<DomainException>(() => world.TasksService.OpenRiskAppeal(taskIds[^1], world.Owner, "第六条"));
        Assert.Contains("近 24 小时提交的申诉已达上限", error.Message);
        Assert.Equal(RiskAppealPolicy.MaxPerOwnerPerDay, world.AppealRepository.CountByOwnerSince(world.Owner, Now - RiskAppealPolicy.Window));
    }

    [Fact]
    public void The_history_keeps_every_appeal_and_its_decision()
    {
        var world = new World();
        var taskId = world.CreateBlocked("帮我代考英语四级");

        world.TasksService.OpenRiskAppeal(taskId, world.Owner, "第一次：只是给家里人帮忙");
        world.Appeals.DecideAppeal(taskId, accepted: false, "理由不成立：标题写的是代考", world.AdminId);
        // 留档按提交时间排序：时钟固定在同一个时刻时两次提交时间相同，顺序只能靠 Id 兜底（那是随机的）。
        // 真实世界里两次申诉本来就有先后，这里把时钟往前推一点，让顺序确定。
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        world.TasksService.UpdateDraft(taskId, world.Owner, world.DraftUpdate("帮我代考英语四级（换了一种说法）"));
        world.TasksService.OpenRiskAppeal(taskId, world.Owner, "第二次：换了一种说法，请再看一次");
        world.Appeals.DecideAppeal(taskId, accepted: true, "确认这次属于误伤写法，按误伤记录", world.AdminId);

        var history = world.Appeals.ListHistory(taskId);

        Assert.Equal(2, history.Items.Count);
        Assert.Equal(RiskAppealPolicy.MaxPerTask, history.MaxPerTask);
        Assert.Equal(RiskAppealPolicy.MaxPerOwnerPerDay, history.MaxPerOwnerPerDay);

        Assert.Equal("第一次：只是给家里人帮忙", history.Items[0].Reason);
        Assert.Equal("Denied", history.Items[0].Status);
        Assert.Equal("prohibited.exam_impersonation", history.Items[0].RuleCode);
        Assert.Equal("理由不成立：标题写的是代考", history.Items[0].DecisionNote);
        Assert.Equal(world.AdminId, history.Items[0].DecidedBy);

        Assert.Equal("第二次：换了一种说法，请再看一次", history.Items[1].Reason);
        Assert.Equal("Accepted", history.Items[1].Status);
        Assert.True(history.Items[0].SubmittedAt <= history.Items[1].SubmittedAt);
    }

    [Fact]
    public void The_queue_tells_operators_how_many_times_a_task_was_appealed()
    {
        var world = new World();
        var taskId = world.CreateBlocked("帮我代考英语四级");
        world.TasksService.OpenRiskAppeal(taskId, world.Owner, "第一次申诉");
        world.Appeals.DecideAppeal(taskId, accepted: false, "维持原判", world.AdminId);
        world.TasksService.UpdateDraft(taskId, world.Owner, world.DraftUpdate("帮我代考英语四级（改了说法）"));
        world.TasksService.OpenRiskAppeal(taskId, world.Owner, "第二次申诉");

        var item = Assert.Single(world.Appeals.ListAppeals(10).Items);

        Assert.Equal(taskId, item.TaskId);
        Assert.Equal(2, item.AppealCount);
    }

    [Fact]
    public void Deciding_an_appeal_writes_the_decision_back_to_the_record()
    {
        var world = new World();
        var taskId = world.CreateBlocked("帮我代考英语四级");
        world.TasksService.OpenRiskAppeal(taskId, world.Owner, "误判");

        world.Appeals.DecideAppeal(taskId, accepted: false, "维持原判，理由不成立", world.AdminId);

        var record = Assert.Single(world.AppealRepository.ListByTask(taskId));
        Assert.Equal(RiskAppealStatus.Denied, record.Status);
        Assert.Equal(world.AdminId, record.DecidedBy);
        Assert.Equal("维持原判，理由不成立", record.DecisionNote);
        // 已经处置过的申诉不再有"待处置"记录。
        Assert.Null(world.AppealRepository.FindPending(taskId));
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();
        private readonly InMemoryOrderRepository orders = new();
        private readonly InMemoryAdminAuditRepository audits = new();
        private readonly InMemoryRiskRuleCatalogStore catalogs = new();

        public World()
        {
            Owner = Guid.NewGuid();
            AdminId = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            AppealRepository = new InMemoryRiskAppealRepository();
            var unitOfWork = new InMemoryUnitOfWork();

            TasksService = new TaskService(tasks, orders, new InMemoryReviewRepository(), new InMemoryTaskRevisionRepository(),
                new EmptyUserDirectory(), Clock, Notifications, unitOfWork,
                riskRuleCatalog: null, riskEnforcement: null, riskAppeals: AppealRepository);
            Appeals = new RiskAppealService(tasks, AppealRepository, audits, new EmptyUserDirectory(), Clock, Notifications, unitOfWork);
        }

        public Guid Owner { get; }
        public Guid AdminId { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public InMemoryRiskAppealRepository AppealRepository { get; }
        public TaskService TasksService { get; }
        public RiskAppealService Appeals { get; }

        /// <summary>建一条一定被规则拦下的草稿（代考是禁止类别，可直接申诉）。</summary>
        public Guid CreateBlocked(string title) =>
            TasksService.Create(new CreateTaskRequest(Owner, title, "考试要过", "朝阳区", Now.AddHours(6), 50, ["按时送达"])).Id;

        public UpdateTaskDraftRequest DraftUpdate(string title) =>
            new(Owner, title, "只是送一份材料", "朝阳区", Now.AddHours(6), 50, ["按时送达"]);
    }
}
