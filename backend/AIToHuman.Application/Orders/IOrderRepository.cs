using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

public interface IOrderRepository
{
    Order? Get(Guid id);
    Order? GetByTask(Guid taskId);
    void Add(Order order);
}
