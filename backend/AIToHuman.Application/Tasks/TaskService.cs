using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Tasks;
using System.Text;
using AIToHuman.Application.Common;
using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Common;
using AIToHuman.Application.Notifications;

namespace AIToHuman.Application.Tasks;

public sealed class TaskService(ITaskRepository repository, IOrderRepository orderRepository, IReviewRepository reviewRepository, TimeProvider timeProvider, NotificationService notifications, IUnitOfWork unitOfWork)
{
    public TaskResponse Create(CreateTaskRequest request)
    {
        var task = new TaskItem(request.OwnerId, request.Title, request.Description, request.District, request.Deadline, new Money(request.Reward), request.AcceptanceCriteria, timeProvider.GetUtcNow(), request.ExecutionAddress);
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
        var now = timeProvider.GetUtcNow();
        Order? order = null;

        // 并发选人时靠任务行的乐观并发令牌拦住后到的请求：第二次保存会因版本变化抛冲突，
        // 整个事务回滚，不会留下“两个订单”或“任务已分配但没有订单”的中间状态。
        // 通知也写在同一事务内（Outbox），进程重启后由后台任务补发。
        unitOfWork.Execute(() =>
        {
            repository.Save(task);
            order = new Order(task.Id, task.OwnerId, selected.WorkerId, task.Title, task.Reward, now);
            orderRepository.Add(order);
            notifications.EnqueueOrderCreated(order, now);
        });

        return new(Map(task), Map(order!));
    }

    public IReadOnlyCollection<TaskApplicationResponse> ListApplications(Guid id, Guid ownerId)
    {
        var task = GetOwned(id, ownerId);
        return task.Applications.Select(MapApplication).ToArray();
    }

    public IReadOnlyCollection<TaskSummaryResponse> ListMine(Guid ownerId) => repository.ListByOwner(ownerId).Select(MapSummary).ToArray();

    /// <summary>
    /// 大厅列表：按区域与悬赏区间筛选，按截止时间升序游标分页。
    /// 多取一条判断是否还有下一页，因此返回的游标一定指向下一页的起点。
    /// </summary>
    public TaskListResponse SearchPublished(TaskSearchRequest request)
    {
        if (request.MinReward is { } min && request.MaxReward is { } max && min > max)
            throw new ArgumentException("悬赏区间的最小值不能大于最大值。");
        if (request.MinReward is < 0 || request.MaxReward is < 0)
            throw new ArgumentException("悬赏区间不能为负数。");

        var limit = request.Limit is < 1 or > MaxPageSize ? DefaultPageSize : request.Limit;
        var cursor = DecodeCursor(request.Cursor);
        var rows = repository.ListPublished(new PublishedTaskFilter(
            string.IsNullOrWhiteSpace(request.District) ? null : request.District.Trim(),
            request.MinReward,
            request.MaxReward,
            cursor?.Deadline,
            cursor?.Id,
            limit + 1)).ToArray();

        var hasMore = rows.Length > limit;
        var page = hasMore ? rows[..limit] : rows;
        return new(page.Select(MapSummary).ToArray(), hasMore ? EncodeCursor(page[^1]) : null, hasMore);
    }

    public const int MaxPageSize = 50;
    private const int DefaultPageSize = 12;

    private static string EncodeCursor(TaskItem task) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{task.Deadline.UtcTicks}|{task.Id:N}"));

    private static (DateTimeOffset Deadline, Guid Id)? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            if (parts.Length == 2 && long.TryParse(parts[0], out var ticks) && Guid.TryParseExact(parts[1], "N", out var id))
                return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (FormatException)
        {
            // 落到下面统一报错。
        }

        throw new ArgumentException("分页游标无效，请重新加载列表。");
    }

    public TaskResponse? Get(Guid id) => repository.Get(id) is { } task ? Map(task) : null;

    /// <summary>公开详情：草稿只对所有者可见，其他人的草稿按「不存在」处理，避免草稿内容泄露。</summary>
    public TaskSummaryResponse? GetPublic(Guid id, Guid? viewerId)
    {
        var task = repository.Get(id);
        if (task is null) return null;
        if (task.Status == AIToHuman.Domain.Tasks.TaskStatus.ReadyToPublish && task.OwnerId != viewerId) return null;
        return MapSummary(task);
    }

    /// <summary>分阶段披露：只有所有者与被选中的服务者能读取执行地址，其他人返回 403。</summary>
    public string? GetExecutionAddress(Guid id, Guid viewerId)
    {
        var task = repository.Get(id) ?? throw new KeyNotFoundException("任务不存在。");
        var address = task.ExecutionAddressFor(viewerId);
        if (address is null && task.HasExecutionAddress) throw new UnauthorizedAccessException("执行地址仅在订单成立后向参与者披露。");
        return address;
    }
    /// <summary>按任务查询订单（路由是 /tasks/{id}/order，因此这里的 id 是任务 ID）。</summary>
    public OrderResponse? GetOrderByTask(Guid taskId, Guid userId)
    {
        var order = orderRepository.GetByTask(taskId);
        if (order is null) return null;
        if (order.OwnerId != userId && order.WorkerId != userId) throw new UnauthorizedAccessException("只有订单参与者可以查看订单。");
        return Map(order);
    }
    public IReadOnlyCollection<OrderResponse> ListOrders(Guid userId) => orderRepository.ListByUser(userId).Select(Map).ToArray();
    public OrderResponse StartOrder(Guid id, Guid actorId) => TransitionOrder(id, actorId, order => order.Start(actorId));
    public OrderResponse SubmitOrder(Guid id, Guid actorId, string? note) => TransitionOrder(id, actorId, order => order.Submit(actorId, note, timeProvider.GetUtcNow()));
    public OrderResponse ApproveOrder(Guid id, Guid actorId, string? note)
    {
        var order = orderRepository.Get(id) ?? throw new KeyNotFoundException("订单不存在。");
        order.Approve(actorId, note, timeProvider.GetUtcNow());
        var now = timeProvider.GetUtcNow();

        // 验收通过要同时写订单状态、关闭任务并写入通知，必须落在同一个事务里。
        unitOfWork.Execute(() =>
        {
            orderRepository.Save(order);
            CloseTaskIfAssigned(order.TaskId);
            notifications.EnqueueOrderStatusChanged(order, order.WorkerId, now);
        });

        return Map(order);
    }
    public OrderResponse RejectOrder(Guid id, Guid actorId, string? note) => TransitionOrder(id, actorId, order => order.Reject(actorId, note, timeProvider.GetUtcNow()));
    public OrderResponse ResumeOrder(Guid id, Guid actorId) => TransitionOrder(id, actorId, order => order.ResumeRework(actorId));

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
        // 每方每个订单只能评价一次；这里先判定，避免依赖数据库唯一约束抛出 500。
        if (reviewRepository.GetByReviewer(orderId, actorId) is not null) throw new DomainException("你已经评价过该订单。");
        var revieweeId = actorId == order.OwnerId ? order.WorkerId : order.OwnerId;
        var review = new Review(orderId, actorId, revieweeId, request.Rating, request.Comment, timeProvider.GetUtcNow());
        reviewRepository.Add(review);
        return MapReview(review, true);
    }

    public ReviewSummaryResponse GetReviewSummary(Guid userId)
    {
        var visible = reviewRepository.ListByReviewee(userId).Where(item => IsReviewPublic(item.OrderId, item.CreatedAt)).ToArray();
        var average = visible.Length == 0 ? 0m : Math.Round((decimal)visible.Average(item => item.Rating), 1);
        return new(userId, average, visible.Length, visible.Take(5).Select(item => MapReview(item, true)).ToArray());
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
        var now = timeProvider.GetUtcNow();
        var recipient = actorId == order.OwnerId ? order.WorkerId : order.OwnerId;

        // 状态写入与通知入队同事务，保证“状态变了但通知丢了”不会发生。
        unitOfWork.Execute(() =>
        {
            orderRepository.Save(order);
            notifications.EnqueueOrderStatusChanged(order, recipient, now);
        });

        return Map(order);
    }

    /// <summary>订单验收通过后同步关闭任务。历史数据里任务可能已关闭，此处只处理仍处于 Assigned 的任务。</summary>
    private void CloseTaskIfAssigned(Guid taskId)
    {
        if (repository.Get(taskId) is not { } task) return;
        if (task.Status != AIToHuman.Domain.Tasks.TaskStatus.Assigned) return;
        task.Close();
        repository.Save(task);
    }

    private static TaskResponse Map(TaskItem task) => new(task.Id, task.OwnerId, task.Title, task.Description, task.District, task.Deadline, task.Reward.Amount, task.Reward.Currency, task.Status.ToString(), task.AcceptanceCriteria, task.Applications.Select(MapApplication).ToArray());
    private static TaskApplicationResponse MapApplication(TaskApplication application) => new(application.Id, application.WorkerId, application.Note, application.Status.ToString(), application.SubmittedAt);

    private static TaskSummaryResponse MapSummary(TaskItem task) => new(task.Id, task.OwnerId, task.Title, task.Description, task.District, task.Deadline, task.Reward.Amount, task.Reward.Currency, task.Status.ToString(), task.AcceptanceCriteria, task.Applications.Count, task.HasExecutionAddress);
    private static OrderResponse Map(Order order) => new(order.Id, order.TaskId, order.OwnerId, order.WorkerId, order.Title, order.Reward.Amount, order.Reward.Currency, order.Status.ToString(), order.CreatedAt, order.EvidenceNote, order.ReviewNote, order.SubmittedAt, order.ReviewedAt, order.ReworkCount, order.RejectionNote);
    private static ReviewResponse MapReview(Review review, bool visible) => new(review.Id, review.OrderId, review.ReviewerId, review.RevieweeId, review.Rating, visible ? review.Comment : "评价将在双方完成后公开", review.CreatedAt, visible);
    private static void EnsureParticipant(Order order, Guid actorId) { if (order.OwnerId != actorId && order.WorkerId != actorId) throw new UnauthorizedAccessException("只有订单参与者可以执行该操作。"); }
    private bool IsReviewPublic(Guid orderId, DateTimeOffset createdAt)
    {
        var reviews = reviewRepository.ListByOrder(orderId);
        return reviews.Count >= 2 || timeProvider.GetUtcNow() >= reviews.Min(item => item.CreatedAt).AddDays(7);
    }
}
