using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 报名列表带服务者公开信用：需求方在选人前就能看到评分与样本量——
/// 这是 ADR-0002 承诺的双向选择里"用户看服务者"的那一半，之前只有服务者能看到需求方信用。
/// 盲期内的评价一律不计入摘要。
/// </summary>
public sealed class WorkerCreditInApplicationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_applications_list_carries_the_workers_public_rating_and_sample_size()
    {
        var world = new World();
        world.CompleteOrderWithReview(rating: 5);
        world.CompleteOrderWithReview(rating: 4);
        world.Clock.Advance(TimeSpan.FromDays(8)); // 单方评价要到 7 天后才公开

        var (taskId, ownerId) = world.OpenTaskWithApplication();
        var credit = world.Applications(taskId, ownerId).Single(item => item.WorkerId == world.Worker);

        Assert.Equal(4.5m, credit.WorkerAverageRating);
        Assert.Equal(2, credit.WorkerReviewCount);
    }

    [Fact]
    public void Reviews_still_in_the_blind_period_are_not_counted()
    {
        var world = new World();
        world.CompleteOrderWithReview(rating: 5);

        var (taskId, ownerId) = world.OpenTaskWithApplication();
        var credit = world.Applications(taskId, ownerId).Single(item => item.WorkerId == world.Worker);

        // 评价刚提交、还没满足公开条件：列表里不能出现分数，否则盲期等于没有。
        Assert.Equal(0m, credit.WorkerAverageRating);
        Assert.Equal(0, credit.WorkerReviewCount);
    }

    [Fact]
    public void A_worker_without_public_reviews_reports_a_zero_sample_instead_of_a_fake_score()
    {
        var world = new World();

        var (taskId, ownerId) = world.OpenTaskWithApplication();
        var credit = world.Applications(taskId, ownerId).Single(item => item.WorkerId == world.Worker);

        Assert.Equal(0, credit.WorkerReviewCount);
        Assert.Equal(0m, credit.WorkerAverageRating);
    }

    [Fact]
    public void Each_application_carries_its_own_workers_credit()
    {
        var world = new World();
        world.CompleteOrderWithReview(rating: 5, worker: world.Worker);
        world.CompleteOrderWithReview(rating: 5, worker: world.Worker);
        world.Clock.Advance(TimeSpan.FromDays(8));

        var (taskId, ownerId) = world.OpenTaskWithApplication(worker: world.Worker);
        var newcomer = Guid.NewGuid();
        world.Apply(taskId, newcomer);

        var applications = world.Applications(taskId, ownerId);

        var rated = applications.Single(item => item.WorkerId == world.Worker);
        var fresh = applications.Single(item => item.WorkerId == newcomer);
        Assert.Equal(2, rated.WorkerReviewCount);
        Assert.Equal(5m, rated.WorkerAverageRating);
        // 新服务者没有公开评价：不能被别人的分数串到。
        Assert.Equal(0, fresh.WorkerReviewCount);
        Assert.Equal(0m, fresh.WorkerAverageRating);
    }

    [Fact]
    public void The_applications_list_agrees_with_the_public_review_summary_endpoint()
    {
        var world = new World();
        world.CompleteOrderWithReview(rating: 3);
        world.Clock.Advance(TimeSpan.FromDays(8));
        var (taskId, ownerId) = world.OpenTaskWithApplication();

        var fromApplications = world.Applications(taskId, ownerId).Single(item => item.WorkerId == world.Worker);
        var fromPublicSummary = world.Service.GetReviewSummary(world.Worker);

        Assert.Equal(fromPublicSummary.AverageRating, fromApplications.WorkerAverageRating);
        Assert.Equal(fromPublicSummary.ReviewCount, fromApplications.WorkerReviewCount);
    }

    private sealed class World
    {
        private readonly InMemoryTaskRepository tasks = new();

        public World()
        {
            Worker = Guid.NewGuid();
            Clock = new MutableTimeProvider(Now);
            Notifications = new NotificationService(new InMemoryNotificationRepository(), Clock);
            Service = new TaskService(tasks, new InMemoryOrderRepository(), new InMemoryReviewRepository(), Clock, Notifications, new InMemoryUnitOfWork());
        }

        public Guid Worker { get; }
        public MutableTimeProvider Clock { get; }
        public NotificationService Notifications { get; }
        public TaskService Service { get; }

        /// <summary>走完一单，并让需求方给服务者打分（评价是否公开由 7 天规则决定）。</summary>
        public void CompleteOrderWithReview(int rating, Guid? worker = null)
        {
            var effectiveWorker = worker ?? Worker;
            var owner = Guid.NewGuid();
            var task = Service.Create(new CreateTaskRequest(owner, "代取文件", "到前台取一份普通文件", "浦东新区", Clock.GetUtcNow().AddHours(6), 50, ["上传取件码照片"]));
            Service.Publish(task.Id, owner);
            var applied = Service.Apply(task.Id, new ApplyForTaskRequest(effectiveWorker, "半小时可到"));
            var orderId = Service.Select(task.Id, applied.Applications.Single().Id, new SelectApplicationRequest(owner)).Order.Id;
            Service.StartOrder(orderId, effectiveWorker);
            Service.SubmitOrder(orderId, effectiveWorker, "已完成");
            Service.ApproveOrder(orderId, owner, "验收通过");
            // 需求方评价服务者：被评价人是服务者，因此进的是他的信用摘要。
            Service.CreateReview(orderId, new CreateReviewRequest(owner, rating, "合作顺利"), owner);
        }

        /// <summary>开一条正在等需求方挑人的任务，并让目标服务者报名。</summary>
        public (Guid TaskId, Guid OwnerId) OpenTaskWithApplication(Guid? worker = null)
        {
            var owner = Guid.NewGuid();
            var task = Service.Create(new CreateTaskRequest(owner, "帮我去图书馆还两本书", "还到一楼自助机", "海淀区", Clock.GetUtcNow().AddHours(6), 40, ["还书成功并拍照"]));
            Service.Publish(task.Id, owner);
            Apply(task.Id, worker ?? Worker);
            return (task.Id, owner);
        }

        public void Apply(Guid taskId, Guid worker) => Service.Apply(taskId, new ApplyForTaskRequest(worker, "可以接"));

        /// <summary>所有者视角的报名列表（含每条报名对应的服务者信用）。</summary>
        public IReadOnlyCollection<TaskApplicationResponse> Applications(Guid taskId, Guid ownerId) =>
            Service.ListApplications(taskId, ownerId);
    }
}
