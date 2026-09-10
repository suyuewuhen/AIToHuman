using AIToHuman.Application.Common;
using AIToHuman.Application.Notifications;
using AIToHuman.Contracts.Orders;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

/// <summary>
/// 订单会话：只有订单双方可以读写消息，未读按“对方发来的且未标记已读”计算。
/// 发送消息与写入通知在同一个事务内完成。
/// </summary>
public sealed class OrderChatService(
    IOrderRepository orderRepository,
    IOrderMessageRepository messageRepository,
    NotificationService notifications,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider)
{
    public const int MaxListLimit = 100;

    public OrderMessageListResponse List(Guid orderId, Guid viewerId, int limit)
    {
        EnsureParticipant(orderId, viewerId);
        var effectiveLimit = limit is < 1 or > MaxListLimit ? 50 : limit;
        var items = messageRepository.ListByOrder(orderId, effectiveLimit).Select(Map).ToArray();
        return new(items, messageRepository.CountUnread(orderId, viewerId));
    }

    public OrderMessageResponse Send(Guid orderId, Guid senderId, string content)
    {
        var order = EnsureParticipant(orderId, senderId);
        var now = timeProvider.GetUtcNow();
        var message = new OrderMessage(orderId, senderId, content, now);
        var recipientId = senderId == order.OwnerId ? order.WorkerId : order.OwnerId;

        // 消息与通知同事务写入，避免“消息发出去了但对方收不到提醒”。
        unitOfWork.Execute(() =>
        {
            messageRepository.Add(message);
            notifications.EnqueueOrderMessage(order, message, recipientId, now);
        });

        return Map(message);
    }

    public int MarkRead(Guid orderId, Guid viewerId)
    {
        EnsureParticipant(orderId, viewerId);
        messageRepository.MarkRead(orderId, viewerId, timeProvider.GetUtcNow());
        return messageRepository.CountUnread(orderId, viewerId);
    }

    /// <summary>订单列表用的未读徽标数据。</summary>
    public IReadOnlyDictionary<Guid, int> UnreadByOrder(Guid recipientId, IReadOnlyCollection<Guid> orderIds) =>
        orderIds.Count == 0 ? new Dictionary<Guid, int>() : messageRepository.CountUnreadByRecipient(recipientId, orderIds);

    private Order EnsureParticipant(Guid orderId, Guid viewerId)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        if (order.OwnerId != viewerId && order.WorkerId != viewerId) throw new UnauthorizedAccessException("只有订单参与者可以查看会话。");
        return order;
    }

    private static OrderMessageResponse Map(OrderMessage message) =>
        new(message.Id, message.OrderId, message.SenderId, message.Content, message.CreatedAt, message.ReadAt);
}
