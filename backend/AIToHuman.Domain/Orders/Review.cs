using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Orders;

public sealed class Review
{
    private Review() { }
    public Review(Guid orderId, Guid reviewerId, Guid revieweeId, int rating, string? comment, DateTimeOffset createdAt)
    {
        if (orderId == Guid.Empty || reviewerId == Guid.Empty || revieweeId == Guid.Empty) throw new DomainException("评价必须关联有效的订单和用户。");
        if (reviewerId == revieweeId) throw new DomainException("不能评价自己。");
        if (rating is < 1 or > 5) throw new DomainException("评分必须在 1 到 5 星之间。");
        if (comment?.Length > 1000) throw new DomainException("评价内容不能超过 1000 个字符。");
        Id = Guid.NewGuid(); OrderId = orderId; ReviewerId = reviewerId; RevieweeId = revieweeId; Rating = rating; Comment = comment?.Trim() ?? string.Empty; CreatedAt = createdAt;
    }
    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ReviewerId { get; private set; }
    public Guid RevieweeId { get; private set; }
    public int Rating { get; private set; }
    public string Comment { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public static Review Rehydrate(Guid id, Guid orderId, Guid reviewerId, Guid revieweeId, int rating, string comment, DateTimeOffset createdAt) => new() { Id = id, OrderId = orderId, ReviewerId = reviewerId, RevieweeId = revieweeId, Rating = rating, Comment = comment, CreatedAt = createdAt };
}
