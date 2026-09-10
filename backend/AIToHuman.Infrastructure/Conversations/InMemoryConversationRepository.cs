using System.Collections.Concurrent;
using AIToHuman.Application.Conversations;
using AIToHuman.Domain.Conversations;

namespace AIToHuman.Infrastructure.Conversations;

public sealed class InMemoryConversationRepository : IConversationRepository
{
    private readonly ConcurrentDictionary<Guid, Conversation> conversations = new();

    public Conversation? Get(Guid id) => conversations.GetValueOrDefault(id);
    public IReadOnlyCollection<Conversation> ListByUser(Guid userId, int limit) => conversations.Values.Where(item => item.UserId == userId).OrderByDescending(item => item.UpdatedAt).Take(limit).ToArray();
    public void Add(Conversation conversation) { if (!conversations.TryAdd(conversation.Id, conversation)) throw new InvalidOperationException("对话标识冲突。"); }
    public void Save(Conversation conversation) => conversations[conversation.Id] = conversation;
}
