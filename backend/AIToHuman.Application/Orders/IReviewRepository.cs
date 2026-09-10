using AIToHuman.Domain.Orders;
namespace AIToHuman.Application.Orders;
public interface IReviewRepository
{
    IReadOnlyCollection<Review> ListByOrder(Guid orderId);
    Review? GetByReviewer(Guid orderId, Guid reviewerId);
    void Add(Review review);
}
