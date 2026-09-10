using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Tasks;
using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Common;
using AIToHuman.Application.Notifications;

namespace AIToHuman.Application.Tasks;

public sealed class TaskService(ITaskRepository repository, IOrderRepository orderRepository, IReviewRepository reviewRepository, TimeProvider timeProvider, IOrderNotificationPublisher notificationPublisher)
{
    public TaskResponse Create(CreateTaskRequest request)
    {
        var task = new TaskItem(request.OwnerId, request.Title, request.Description, request.District, request.Deadline, new Money(request.Reward), request.AcceptanceCriteria, timeProvider.GetUtcNow());
        repository.Add(task);
        return Map(task);
    }

    public TaskResponse Publish(Guid id, Guid ownerId)
    {
        var task = GetOwned(id, ownerId);
        task.Publish(timeProvider.GetUtcNow());
        repository.Save(task);
        return Map(task);
    }

    public TaskResponse IncreaseReward(Guid id, Guid ownerId, IncreaseRewardRequest request)
    {
        var task = GetOwned(id, ownerId);
        task.IncreaseReward(new Money(request.Reward));
        repository.Save(task);
        return Map(task);
    }

    public TaskResponse Apply(Guid id, ApplyForTaskRequest request)
    {
        var task = GetRequired(id);
        task.Apply(request.WorkerId, request.Note, timeProvider.GetUtcNow());
        repository.Save(task);
        return Map(task);
    }

    public SelectTaskResult Select(Guid id, Guid applicationId, SelectApplicationRequest request)
    {
        var task = GetOwned(id, request.OwnerId);
        var selected = task.SelectApplication(applicationId);
        repository.Save(task);
        var order = new Order(task.Id, task.OwnerId, selected.WorkerId, task.Title, task.Reward, timeProvider.GetUtcNow());
        orderRepository.Add(order);
        _ = notificationPublisher.PublishOrderCreatedAsync(selected.WorkerId, Map(order));
        return new(Map(task), Map(order));
    }

    public IReadOnlyCollection<TaskApplicationResponse> ListApplications(Guid id, Guid ownerId)
    {
        var task = GetOwned(id, ownerId);
        return task.Applications.Select(MapApplication).ToArray();
    }

    public IReadOnlyCollection<TaskSummaryResponse> ListPublished() => repository.ListPublished().Select(MapSummary).ToArray();
    public TaskResponse? Get(Guid id) => repository.Get(id) is { } task ? Map(task) : null;
    public TaskSummaryResponse? GetPublic(Guid id) => repository.Get(id) is { } task ? MapSummary(task) : null;
    public OrderResponse? GetOrder(Guid id, Guid userId)
    {
        var order = orderRepository.Get(id);
        if (order is null) return null;
        if (order.OwnerId != userId && order.WorkerId != userId) throw new UnauthorizedAccessException("只有订单参与者可以查看订单。");
        return Map(order);
    }
    public IReadOnlyCollection<OrderResponse> ListOrders(Guid userId) => orderRepository.ListByUser(userId).Select(Map).ToArray();
    public OrderResponse StartOrder(Guid id, Guid actorId) => TransitionOrder(id, actorId, order => order.Start(actorId));
    public OrderResponse SubmitOrder(Guid id, Guid actorId, string? note) => TransitionOrder(id, actorId, order => order.Submit(actorId, note, timeProvider.GetUtcNow()));
    public OrderResponse ApproveOrder(Guid id, Guid actorId, string? note) => TransitionOrder(id, actorId, order => order.Approve(actorId, note, timeProvider.GetUtcNow()));
    public OrderResponse RejectOrder(Guid id, Guid actorId, string? note) => TransitionOrder(id, actorId, order => order.Reject(actorId, note, timeProvider.GetUtcNow()));

    public IReadOnlyCollection<ReviewResponse> ListReviews(Guid orderId, Guid viewerId)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        EnsureParticipant(order, viewerId);
        var reviews = reviewRepository.ListByOrder(orderId);
        var revealAt = reviews.Count == 0 ? DateTimeOffset.MaxValue : reviews.Min(item => item.CreatedAt).AddDays(7);
        var visible = reviews.Count >= 2 || timeProvider.GetUtcNow() >= revealAt;
        return reviews.Select(item => MapReview(item, visible || item.ReviewerId == viewerId)).ToArray();
    }

    public ReviewResponse CreateReview(Guid orderId, CreateReviewRequest request, Guid actorId)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        EnsureParticipant(order, actorId);
        if (order.Status != OrderStatus.Approved) throw new DomainException("只有已完成订单才能评价。");
        var revieweeId = actorId == order.OwnerId ? order.WorkerId : order.OwnerId;
        var review = new Review(orderId, actorId, revieweeId, request.Rating, request.Comment, timeProvider.GetUtcNow());
        reviewRepository.Add(review);
        return MapReview(review, true);
    }

    public RewardSuggestionResponse SuggestReward(RewardSuggestionRequest request)
    {
        var travel = (decimal)Math.Max(request.DistanceKilometers, 0) * 2.2m;
        var labor = Math.Max(request.EstimatedMinutes, 15) / 60m * 24m;
        var peak = request.IsPeakHours ? 8m : 0m;
        var category = request.Category.Equals("queue", StringComparison.OrdinalIgnoreCase) ? 10m : 5m;
        var suggested = Math.Ceiling(Math.Max(20m, travel + labor + peak + category) / 5m) * 5m;
        return new(suggested, Math.Max(15m, suggested - 10m), suggested + 20m, "CNY", ["预计距离", "预计耗时", request.IsPeakHours ? "高峰时段" : "普通时段", "任务类别"], "cold-start");
    }

    private TaskItem GetOwned(Guid id, Guid ownerId)
    {
        var task = GetRequired(id);
        if (task.OwnerId != ownerId) throw new UnauthorizedAccessException("只有任务所有者可以执行该操作。");
        return task;
    }

    private TaskItem GetRequired(Guid id) => repository.Get(id) ?? throw new KeyNotFoundException("任务不存在。");

    private OrderResponse TransitionOrder(Guid id, Guid actorId, Action<Order> transition)
    {
        var order = orderRepository.Get(id) ?? throw new KeyNotFoundException("订单不存在。");
        transition(order);
        orderRepository.Save(order);
        return Map(order);
    }

    private static TaskResponse Map(TaskItem task) => new(task.Id, task.OwnerId, task.Title, task.Description, task.District, task.Deadline, task.Reward.Amount, task.Reward.Currency, task.Status.ToString(), task.AcceptanceCriteria, task.Applications.Select(MapApplication).ToArray());

    private static TaskApplicationResponse MapApplication(TaskApplication application) => new(application.Id, application.WorkerId, application.Note, application.Status.ToString(), application.SubmittedAt);

    private static TaskSummaryResponse MapSummary(TaskItem task) => new(task.Id, task.OwnerId, task.Title, task.Description, task.District, task.Deadline, task.Reward.Amount, task.Reward.Currency, task.Status.ToString(), task.AcceptanceCriteria, task.Applications.Count);
    private static OrderResponse Map(Order order) => new(order.Id, order.TaskId, order.OwnerId, order.WorkerId, order.Title, order.Reward.Amount, order.Reward.Currency, order.Status.ToString(), order.CreatedAt, order.EvidenceNote, order.ReviewNote, order.SubmittedAt, order.ReviewedAt);
    private static ReviewResponse MapReview(Review review, bool visible) => new(review.Id, review.OrderId, review.ReviewerId, review.RevieweeId, review.Rating, visible ? review.Comment : "评价将在双方完成后公开", review.CreatedAt, visible);
    private static void EnsureParticipant(Order order, Guid actorId) { if (order.OwnerId != actorId && order.WorkerId != actorId) throw new UnauthorizedAccessException("只有订单参与者可以执行该操作。"); }
}
