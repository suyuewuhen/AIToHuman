using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Orders;

public sealed class EfOrderMessageRepository(TaskDbContext db) : IOrderMessageRepository
{
    public IReadOnlyCollection<OrderMessage> ListByOrder(Guid orderId, int limit) => db.OrderMessages
        .AsNoTracking()
        .Where(item => item.OrderId == orderId)
        .OrderByDescending(item => item.CreatedAt)
        .ThenByDescending(item => item.Id)
        .Take(limit)
        .AsEnumerable()
        .Reverse()
        .Select(Map)
        .ToArray();

    public int CountUnread(Guid orderId, Guid recipientId) => db.OrderMessages
        .AsNoTracking()
        .Count(item => item.OrderId == orderId && item.SenderId != recipientId && item.ReadAt == null);

    public IReadOnlyDictionary<Guid, int> CountUnreadByRecipient(Guid recipientId, IReadOnlyCollection<Guid> orderIds) => db.OrderMessages
        .AsNoTracking()
        .Where(item => orderIds.Contains(item.OrderId) && item.SenderId != recipientId && item.ReadAt == null)
        .GroupBy(item => item.OrderId)
        .Select(group => new { OrderId = group.Key, Count = group.Count() })
        .ToDictionary(item => item.OrderId, item => item.Count);

    public void Add(OrderMessage message)
    {
        db.OrderMessages.Add(ToRecord(message));
        db.SaveChanges();
    }

    public int MarkRead(Guid orderId, Guid readerId, DateTimeOffset now) => db.OrderMessages
        .Where(item => item.OrderId == orderId && item.SenderId != readerId && item.ReadAt == null)
        .ExecuteUpdate(setters => setters.SetProperty(item => item.ReadAt, now));

    private static OrderMessageRecord ToRecord(OrderMessage message) => new()
    {
        Id = message.Id,
        OrderId = message.OrderId,
        SenderId = message.SenderId,
        Content = message.Content,
        CreatedAt = message.CreatedAt,
        ReadAt = message.ReadAt
    };

    private static OrderMessage Map(OrderMessageRecord record) =>
        OrderMessage.Rehydrate(record.Id, record.OrderId, record.SenderId, record.Content, record.CreatedAt, record.ReadAt);
}

public sealed class InMemoryOrderMessageRepository : IOrderMessageRepository
{
    private readonly List<OrderMessage> messages = [];
    private readonly Lock gate = new();

    public IReadOnlyCollection<OrderMessage> ListByOrder(Guid orderId, int limit)
    {
        lock (gate)
        {
            return messages
                .Where(item => item.OrderId == orderId)
                .OrderByDescending(item => item.CreatedAt)
                .Take(limit)
                .OrderBy(item => item.CreatedAt)
                .ToArray();
        }
    }

    public int CountUnread(Guid orderId, Guid recipientId)
    {
        lock (gate)
        {
            return messages.Count(item => item.OrderId == orderId && item.IsUnreadFor(recipientId));
        }
    }

    public IReadOnlyDictionary<Guid, int> CountUnreadByRecipient(Guid recipientId, IReadOnlyCollection<Guid> orderIds)
    {
        lock (gate)
        {
            return messages
                .Where(item => orderIds.Contains(item.OrderId) && item.IsUnreadFor(recipientId))
                .GroupBy(item => item.OrderId)
                .ToDictionary(group => group.Key, group => group.Count());
        }
    }

    public void Add(OrderMessage message) { lock (gate) { messages.Add(message); } }

    public int MarkRead(Guid orderId, Guid readerId, DateTimeOffset now)
    {
        lock (gate)
        {
            var unread = messages.Where(item => item.OrderId == orderId && item.IsUnreadFor(readerId)).ToArray();
            foreach (var message in unread) message.MarkRead(now);
            return unread.Length;
        }
    }
}
