using System.Collections.Concurrent;
using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Infrastructure.Orders;

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> orders = new();
    public Order? Get(Guid id) => orders.GetValueOrDefault(id);
    public Order? GetByTask(Guid taskId) => orders.Values.SingleOrDefault(item => item.TaskId == taskId);
    public void Add(Order order) { if (!orders.TryAdd(order.Id, order)) throw new InvalidOperationException("订单标识冲突。"); }
}
