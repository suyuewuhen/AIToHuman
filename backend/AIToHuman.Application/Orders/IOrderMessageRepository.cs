using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

public interface IOrderMessageRepository
{
    IReadOnlyCollection<OrderMessage> ListByOrder(Guid orderId, int limit);

    /// <summary>某个参与者在该订单会话里的未读数。</summary>
    int CountUnread(Guid orderId, Guid recipientId);

    /// <summary>批量取未读数，供订单列表展示徽标，避免逐单查询。</summary>
    IReadOnlyDictionary<Guid, int> CountUnreadByRecipient(Guid recipientId, IReadOnlyCollection<Guid> orderIds);

    void Add(OrderMessage message);

    /// <summary>把该订单里由对方发出且未读的消息标记为已读，返回受影响行数。</summary>
    int MarkRead(Guid orderId, Guid readerId, DateTimeOffset now);
}
