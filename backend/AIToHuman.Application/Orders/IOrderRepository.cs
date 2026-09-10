using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

public interface IOrderRepository
{
    Order? Get(Guid id);
    Order? GetByTask(Guid taskId);
    IReadOnlyCollection<Order> ListByUser(Guid userId);
    void Add(Order order);
    void Save(Order order);
}
