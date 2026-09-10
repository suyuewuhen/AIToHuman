using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Orders;

public sealed class EfOrderRepository(TaskDbContext db) : IOrderRepository
{
    public Order? Get(Guid id) => db.Orders.AsNoTracking().SingleOrDefault(item => item.Id == id) is { } record ? Map(record) : null;
    public Order? GetByTask(Guid taskId) => db.Orders.AsNoTracking().SingleOrDefault(item => item.TaskId == taskId) is { } record ? Map(record) : null;
    public IReadOnlyCollection<Order> ListByUser(Guid userId) => db.Orders.AsNoTracking().Where(item => item.OwnerId == userId || item.WorkerId == userId).OrderByDescending(item => item.CreatedAt).AsEnumerable().Select(Map).ToArray();
    public void Add(Order order)
    {
        db.Orders.Add(new OrderRecord { Id = order.Id, TaskId = order.TaskId, OwnerId = order.OwnerId, WorkerId = order.WorkerId, Title = order.Title, RewardAmount = order.Reward.Amount, RewardCurrency = order.Reward.Currency, Status = order.Status.ToString(), CreatedAt = order.CreatedAt });
        db.SaveChanges();
    }
    public void Save(Order order)
    {
        var record = db.Orders.Single(item => item.Id == order.Id);
        record.Status = order.Status.ToString();
        db.SaveChanges();
    }
    private static Order Map(OrderRecord record) => Order.Rehydrate(record.Id, record.TaskId, record.OwnerId, record.WorkerId, record.Title, new Money(record.RewardAmount, record.RewardCurrency), Enum.Parse<OrderStatus>(record.Status), record.CreatedAt);
}
