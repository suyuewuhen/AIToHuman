using AIToHuman.Domain.Conversations;

namespace AIToHuman.Application.Conversations;

public interface IConversationRepository
{
    Conversation? Get(Guid id);
    IReadOnlyCollection<Conversation> ListByUser(Guid userId, int limit);
    void Add(Conversation conversation);
    void Save(Conversation conversation);
}
