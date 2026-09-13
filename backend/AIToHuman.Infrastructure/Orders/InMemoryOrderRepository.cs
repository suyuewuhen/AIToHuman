using System.Collections.Concurrent;
using AIToHuman.Application.Admin;
using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Infrastructure.Orders;

public sealed class InMemoryOrderRepository : IOrderRepository, IAdminOrderQuery
{
    private readonly ConcurrentDictionary<Guid, Order> orders = new();
    public Order? Get(Guid id) => orders.GetValueOrDefault(id);
    public Order? GetByTask(Guid taskId) => orders.Values.SingleOrDefault(item => item.TaskId == taskId);
    public IReadOnlyCollection<Order> ListByUser(Guid userId) => orders.Values.Where(item => item.OwnerId == userId || item.WorkerId == userId).OrderByDescending(item => item.CreatedAt).ToArray();
    public void Add(Order order) { if (!orders.TryAdd(order.Id, order)) throw new InvalidOperationException("订单标识冲突。"); }
    public void Save(Order order) => orders[order.Id] = order;

    /// <summary>运营检索：跨参与者，按状态过滤（主要用于找待处置的争议）。</summary>
    public IReadOnlyCollection<Order> Search(OrderStatus? status, int limit) => orders.Values
        .Where(order => status is null || order.Status == status)
        .OrderByDescending(order => order.CreatedAt)
        .ThenBy(order => order.Id)
        .Take(limit)
        .ToArray();
}
