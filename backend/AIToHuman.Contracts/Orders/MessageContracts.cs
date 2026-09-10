namespace AIToHuman.Contracts.Orders;

public sealed record OrderMessageResponse(Guid Id, Guid OrderId, Guid SenderId, string Content, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record OrderMessageListResponse(IReadOnlyList<OrderMessageResponse> Items, int UnreadCount);

public sealed record SendOrderMessageRequest(Guid? SenderId, string Content);

/// <summary>聊天通知载荷：只带预览，完整内容通过 REST 拉取。</summary>
public sealed record OrderMessageNotificationPayload(Guid OrderId, string Title, string Preview);
