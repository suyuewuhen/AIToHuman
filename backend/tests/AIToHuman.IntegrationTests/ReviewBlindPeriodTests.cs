using AIToHuman.Application.Notifications;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Common;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Tasks;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 双向评价的盲期与公开规则：双方都提交、或首条评价满 7 天前，对方看不到本单评分与正文；
/// 公开摘要只统计已公开的评价。使用可推进的时钟，不依赖真实等待。
/// </summary>
public sealed class ReviewBlindPeriodTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private const string HiddenComment = "评价将在双方完成后公开";

    [Fact]
    public void First_review_is_visible_to_its_author_but_hidden_from_the_other_party()
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();
        world.ReviewOwner(5, "服务很准时");

        var ownerView = world.Service.ListReviews(world.OrderId, world.Owner);
        var workerView = world.Service.ListReviews(world.OrderId, world.Worker);

        var mine = Assert.Single(ownerView);
        Assert.True(mine.IsVisible);
        Assert.Equal("服务很准时", mine.Comment);

        var theirs = Assert.Single(workerView);
        Assert.False(theirs.IsVisible);
        Assert.Equal(HiddenComment, theirs.Comment);
        // 盲期内对方仍能看到自己被打了几分，只是看不到正文与对方身份以外的内容。
        Assert.Equal(5, theirs.Rating);
    }

    [Fact]
    public void Both_reviews_become_visible_as_soon_as_the_second_one_arrives()
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();
        world.ReviewOwner(5, "服务很准时");
        world.ReviewWorker(4, "需求说明清楚");

        var ownerView = world.Service.ListReviews(world.OrderId, world.Owner);
        var workerView = world.Service.ListReviews(world.OrderId, world.Worker);

        Assert.Equal(2, ownerView.Count);
        Assert.All(ownerView, item => Assert.True(item.IsVisible));
        Assert.Contains(ownerView, item => item.Comment == "需求说明清楚");
        Assert.All(workerView, item => Assert.True(item.IsVisible));
    }

    [Fact]
    public void Single_review_becomes_visible_after_seven_days()
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();
        world.ReviewOwner(5, "服务很准时");

        world.Clock.Advance(TimeSpan.FromDays(7).Subtract(TimeSpan.FromMinutes(1)));
        Assert.All(world.Service.ListReviews(world.OrderId, world.Worker), item => Assert.False(item.IsVisible));

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var revealed = Assert.Single(world.Service.ListReviews(world.OrderId, world.Worker));
        Assert.True(revealed.IsVisible);
        Assert.Equal("服务很准时", revealed.Comment);
    }

    [Fact]
    public void Public_summary_only_counts_revealed_reviews()
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();
        world.ReviewOwner(5, "服务很准时");

        // 盲期内：服务者的公开摘要既没有分数也没有样本量。
        var hidden = world.Service.GetReviewSummary(world.Worker);
        Assert.Equal(0, hidden.ReviewCount);
        Assert.Equal(0m, hidden.AverageRating);
        Assert.Empty(hidden.RecentReviews);

        world.ReviewWorker(3, "需求说明清楚");

        var workerSummary = world.Service.GetReviewSummary(world.Worker);
        Assert.Equal(1, workerSummary.ReviewCount);
        Assert.Equal(5m, workerSummary.AverageRating);
        Assert.Single(workerSummary.RecentReviews);

        var ownerSummary = world.Service.GetReviewSummary(world.Owner);
        Assert.Equal(1, ownerSummary.ReviewCount);
        Assert.Equal(3m, ownerSummary.AverageRating);
    }

    [Fact]
    public void Summary_aggregates_multiple_orders_and_reports_sample_size()
    {
        var world = new ReviewWorld();
        var worker = world.NewWorker();
        world.CreateApprovedOrder(worker: worker);
        world.ReviewOwner(5, "第一次合作很好");
        world.ReviewWorker(4, "需求清楚");

        world.CreateApprovedOrder(worker: worker);
        world.ReviewOwner(4, "第二次也不错");
        world.ReviewWorker(5, "依旧清楚");

        // 同一服务者与两位需求方各完成一单，四条评价都已公开。
        var workerSummary = world.Service.GetReviewSummary(worker);

        Assert.Equal(2, workerSummary.ReviewCount);
        Assert.Equal(4.5m, workerSummary.AverageRating);
        Assert.Equal(2, workerSummary.RecentReviews.Count);
    }

    [Fact]
    public void Only_approved_orders_can_be_reviewed()
    {
        var world = new ReviewWorld();
        world.CreateOrderInProgress();

        var error = Assert.Throws<DomainException>(() => world.ReviewOwner(5, "还没完成"));
        Assert.Equal("只有已完成订单才能评价。", error.Message);

        world.StartWork();
        world.Service.SubmitOrder(world.OrderId, world.Worker, "已完成");
        world.Service.ApproveOrder(world.OrderId, world.Owner, "验收通过");
        world.ReviewOwner(5, "很好");

        Assert.Single(world.Service.ListReviews(world.OrderId, world.Owner));
    }

    [Fact]
    public void Each_party_can_review_only_once()
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();
        world.ReviewOwner(5, "很好");

        var error = Assert.Throws<DomainException>(() => world.ReviewOwner(1, "再来一次"));

        Assert.Equal("你已经评价过该订单。", error.Message);
        Assert.Single(world.Service.ListReviews(world.OrderId, world.Owner));
    }

    [Fact]
    public void Non_participants_cannot_read_or_write_reviews()
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();
        var outsider = Guid.NewGuid();

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.ListReviews(world.OrderId, outsider));
        Assert.Throws<UnauthorizedAccessException>(() => world.Service.CreateReview(world.OrderId, new CreateReviewRequest(outsider, 5, "路过"), outsider));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Rating_must_be_between_one_and_five(int rating)
    {
        var world = new ReviewWorld();
        world.CreateApprovedOrder();

        var error = Assert.Throws<DomainException>(() => world.ReviewOwner(rating, "评分越界"));

        Assert.Equal("评分必须在 1 到 5 星之间。", error.Message);
    }

    /// <summary>搭场景的样板集中在这里；<c>Create*</c> 只负责状态，评价由各测试显式提交。</summary>
    private sealed class ReviewWorld
    {
        public ReviewWorld()
        {
            Clock = new MutableTimeProvider(Now);
            Service = new TaskService(
                new InMemoryTaskRepository(),
                new InMemoryOrderRepository(),
                new InMemoryReviewRepository(),
                Clock,
                new NotificationService(new InMemoryNotificationRepository(), Clock),
                new InMemoryUnitOfWork());
        }

        public MutableTimeProvider Clock { get; }
        public TaskService Service { get; }
        public Guid OrderId { get; private set; }
        public Guid Owner { get; private set; }
        public Guid Worker { get; private set; }

        public Guid NewWorker() => Guid.NewGuid();

        public void CreateOrderInProgress(Guid? owner = null, Guid? worker = null)
        {
            Owner = owner ?? Guid.NewGuid();
            Worker = worker ?? Guid.NewGuid();
            var task = Service.Create(new CreateTaskRequest(Owner, "代取文件", "到前台取一份普通文件", "浦东新区", Clock.GetUtcNow().AddHours(6), 50, ["上传取件码照片"]));
            Service.Publish(task.Id, Owner);
            var applied = Service.Apply(task.Id, new ApplyForTaskRequest(Worker, "半小时可到"));
            OrderId = Service.Select(task.Id, applied.Applications.Single().Id, new SelectApplicationRequest(Owner)).Order.Id;
        }

        public void CreateApprovedOrder(Guid? owner = null, Guid? worker = null)
        {
            CreateOrderInProgress(owner, worker);
            StartWork();
            Service.SubmitOrder(OrderId, Worker, "已完成");
            Service.ApproveOrder(OrderId, Owner, "验收通过");
        }

        public void StartWork() => Service.StartOrder(OrderId, Worker);

        public void ReviewOwner(int rating, string comment) =>
            Service.CreateReview(OrderId, new CreateReviewRequest(Owner, rating, comment), Owner);

        public void ReviewWorker(int rating, string comment) =>
            Service.CreateReview(OrderId, new CreateReviewRequest(Worker, rating, comment), Worker);
    }
}
