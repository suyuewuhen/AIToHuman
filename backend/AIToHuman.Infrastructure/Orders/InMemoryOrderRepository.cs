using System.Collections.Concurrent;
using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Infrastructure.Orders;

public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly ConcurrentDictionary<Guid, Order> orders = new();
    public Order? Get(Guid id) => orders.GetValueOrDefault(id);
    public Order? GetByTask(Guid taskId) => orders.Values.SingleOrDefault(item => item.TaskId == taskId);
    public IReadOnlyCollection<Order> ListByUser(Guid userId) => orders.Values.Where(item => item.OwnerId == userId || item.WorkerId == userId).OrderByDescending(item => item.CreatedAt).ToArray();
    public void Add(Order order) { if (!orders.TryAdd(order.Id, order)) throw new InvalidOperationException("订单标识冲突。"); }
    public void Save(Order order) => orders[order.Id] = order;
}
