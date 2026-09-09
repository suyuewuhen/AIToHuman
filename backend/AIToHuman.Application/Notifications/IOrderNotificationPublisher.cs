using AIToHuman.Contracts.Tasks;

namespace AIToHuman.Application.Notifications;

public interface IOrderNotificationPublisher
{
    Task PublishOrderCreatedAsync(Guid workerId, OrderResponse order, CancellationToken cancellationToken = default);
}
