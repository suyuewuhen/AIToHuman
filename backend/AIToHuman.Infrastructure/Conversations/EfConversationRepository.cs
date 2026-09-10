using AIToHuman.Application.Conversations;
using AIToHuman.Domain.Conversations;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Conversations;

public sealed class EfConversationRepository(TaskDbContext db) : IConversationRepository
{
    public Conversation? Get(Guid id) =>
        db.Conversations.AsNoTracking().Include(item => item.Messages).SingleOrDefault(item => item.Id == id) is { } record ? Map(record) : null;

    public IReadOnlyCollection<Conversation> ListByUser(Guid userId, int limit) =>
        db.Conversations.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.UpdatedAt)
            .Take(limit)
            .Include(item => item.Messages)
            .AsEnumerable()
            .Select(Map)
            .ToArray();

    public void Add(Conversation conversation)
    {
        var record = new ConversationRecord
        {
            Id = conversation.Id,
            UserId = conversation.UserId,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.UpdatedAt
        };
        foreach (var message in conversation.Messages)
        {
            record.Messages.Add(ToRecord(conversation.Id, message));
        }

        db.Conversations.Add(record);
        db.SaveChanges();
    }

    public void Save(Conversation conversation)
    {
        var record = db.Conversations.Single(item => item.Id == conversation.Id);
        record.UpdatedAt = conversation.UpdatedAt;

        // 消息写入后不可变，因此这里只做新增与删除。
        // 注意：不能把新记录加到已跟踪的导航集合里，否则 EF 会因为主键已有值而把它当成已存在的行去 UPDATE。
        var stored = db.ConversationMessages.Where(item => item.ConversationId == conversation.Id).ToDictionary(item => item.Id);
        var keptIds = conversation.Messages.Select(item => item.Id).ToHashSet();

        foreach (var message in conversation.Messages.Where(item => !stored.ContainsKey(item.Id)))
        {
            db.ConversationMessages.Add(ToRecord(conversation.Id, message));
        }

        foreach (var (id, orphan) in stored)
        {
            if (!keptIds.Contains(id)) db.ConversationMessages.Remove(orphan);
        }

        db.SaveChanges();
    }

    private static ConversationMessageRecord ToRecord(Guid conversationId, ConversationMessage message) => new()
    {
        Id = message.Id,
        ConversationId = conversationId,
        Role = message.Role.ToString(),
        Content = message.Content,
        Sequence = message.Sequence,
        CreatedAt = message.CreatedAt,
        ReadyToDraft = message.ReadyToDraft,
        PlanJson = message.PlanJson
    };

    private static Conversation Map(ConversationRecord record) => Conversation.Rehydrate(
        record.Id,
        record.UserId,
        record.CreatedAt,
        record.UpdatedAt,
        record.Messages.Select(item => ConversationMessage.Rehydrate(
            item.Id,
            item.ConversationId,
            Enum.Parse<ConversationRole>(item.Role),
            item.Content,
            item.Sequence,
            item.CreatedAt,
            item.ReadyToDraft,
            item.PlanJson)));
}
