using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Orders;

public sealed class EfOrderRepository(TaskDbContext db) : IOrderRepository
{
    // 同任务仓储：Get 保持跟踪，Save 才能用「做状态判断时读到的版本」做并发校验。
    public Order? Get(Guid id) => db.Orders.SingleOrDefault(item => item.Id == id) is { } record ? Map(record) : null;
    public Order? GetByTask(Guid taskId) => db.Orders.AsNoTracking().SingleOrDefault(item => item.TaskId == taskId) is { } record ? Map(record) : null;
    public IReadOnlyCollection<Order> ListByUser(Guid userId) => db.Orders.AsNoTracking().Where(item => item.OwnerId == userId || item.WorkerId == userId).OrderByDescending(item => item.CreatedAt).AsEnumerable().Select(Map).ToArray();
    public void Add(Order order)
    {
        db.Orders.Add(ToRecord(order));
        db.SaveChanges();
    }
    public void Save(Order order)
    {
        var record = db.Orders.Single(item => item.Id == order.Id);
        record.Status = order.Status.ToString();
        record.EvidenceNote = order.EvidenceNote;
        record.ReviewNote = order.ReviewNote;
        record.SubmittedAt = order.SubmittedAt;
        record.ReviewedAt = order.ReviewedAt;
        record.RejectionNote = order.RejectionNote;
        record.ReworkCount = order.ReworkCount;
        // 自增并发令牌：同一订单被并发提交或验收时，后写入者会拿到 0 行更新并抛冲突。
        record.Version += 1;
        db.SaveChanges();
    }
    private static OrderRecord ToRecord(Order order) => new() { Id = order.Id, TaskId = order.TaskId, OwnerId = order.OwnerId, WorkerId = order.WorkerId, Title = order.Title, RewardAmount = order.Reward.Amount, RewardCurrency = order.Reward.Currency, Status = order.Status.ToString(), CreatedAt = order.CreatedAt, EvidenceNote = order.EvidenceNote, ReviewNote = order.ReviewNote, SubmittedAt = order.SubmittedAt, ReviewedAt = order.ReviewedAt, RejectionNote = order.RejectionNote, ReworkCount = order.ReworkCount };
    private static Order Map(OrderRecord record) => Order.Rehydrate(record.Id, record.TaskId, record.OwnerId, record.WorkerId, record.Title, new Money(record.RewardAmount, record.RewardCurrency), Enum.Parse<OrderStatus>(record.Status), record.CreatedAt, record.EvidenceNote, record.ReviewNote, record.SubmittedAt, record.ReviewedAt, record.RejectionNote, record.ReworkCount);
}

public sealed class EfReviewRepository(TaskDbContext db) : IReviewRepository
{
    public IReadOnlyCollection<Review> ListByOrder(Guid orderId) => db.Reviews.AsNoTracking().Where(item => item.OrderId == orderId).OrderBy(item => item.CreatedAt).AsEnumerable().Select(Map).ToArray();
    public IReadOnlyCollection<Review> ListByReviewee(Guid revieweeId) => db.Reviews.AsNoTracking().Where(item => item.RevieweeId == revieweeId).OrderByDescending(item => item.CreatedAt).AsEnumerable().Select(Map).ToArray();
    public Review? GetByReviewer(Guid orderId, Guid reviewerId) => db.Reviews.AsNoTracking().SingleOrDefault(item => item.OrderId == orderId && item.ReviewerId == reviewerId) is { } record ? Map(record) : null;
    public void Add(Review review) { db.Reviews.Add(ToRecord(review)); db.SaveChanges(); }
    private static ReviewRecord ToRecord(Review review) => new() { Id = review.Id, OrderId = review.OrderId, ReviewerId = review.ReviewerId, RevieweeId = review.RevieweeId, Rating = review.Rating, Comment = review.Comment, CreatedAt = review.CreatedAt };
    private static Review Map(ReviewRecord record) => Review.Rehydrate(record.Id, record.OrderId, record.ReviewerId, record.RevieweeId, record.Rating, record.Comment, record.CreatedAt);
}
