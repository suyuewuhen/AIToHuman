using AIToHuman.Application.Notifications;
using AIToHuman.Contracts.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace AIToHuman.Api.Notifications;

public sealed class SignalROrderNotificationPublisher(IHubContext<NotificationsHub> hub) : IOrderNotificationPublisher
{
    public Task PublishOrderCreatedAsync(Guid workerId, OrderResponse order, CancellationToken cancellationToken = default) => hub.Clients.Group($"user:{workerId:N}").SendAsync("OrderCreated", order, cancellationToken);
}
