using System.Collections.Concurrent;
using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
namespace AIToHuman.Infrastructure.Orders;
public sealed class InMemoryReviewRepository : IReviewRepository
{
    private readonly ConcurrentDictionary<Guid, Review> reviews = new();
    public IReadOnlyCollection<Review> ListByOrder(Guid orderId) => reviews.Values.Where(item => item.OrderId == orderId).OrderBy(item => item.CreatedAt).ToArray();
    public IReadOnlyCollection<Review> ListByReviewee(Guid revieweeId) => reviews.Values.Where(item => item.RevieweeId == revieweeId).OrderByDescending(item => item.CreatedAt).ToArray();
    public Review? GetByReviewer(Guid orderId, Guid reviewerId) => reviews.Values.SingleOrDefault(item => item.OrderId == orderId && item.ReviewerId == reviewerId);
    public void Add(Review review) { if (GetByReviewer(review.OrderId, review.ReviewerId) is not null) throw new InvalidOperationException("你已经评价过该订单。"); if (!reviews.TryAdd(review.Id, review)) throw new InvalidOperationException("评价标识冲突。"); }
}
