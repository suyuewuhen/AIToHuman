using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Tasks;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Application.Admin;

/// <summary>运营后台的跨所有者检索：普通仓储方法都按所有者过滤，这里刻意放开。</summary>
public interface IAdminTaskQuery
{
    IReadOnlyCollection<TaskItem> Search(string? keyword, TaskStatus? status, int limit);
}

/// <summary>用户目录（只读）。没有 PostgreSQL 时由空实现兜底，检索结果为空而不是报错。</summary>
public interface IUserDirectory
{
    IReadOnlyCollection<AdminUserView> Search(string? keyword, int limit);

    IReadOnlyDictionary<Guid, AdminUserView> FindMany(IReadOnlyCollection<Guid> ids);
}

public sealed record AdminUserView(Guid Id, string Email, string DisplayName, string Role, DateTimeOffset CreatedAt);

/// <summary>运营操作审计：只追加。</summary>
public interface IAdminAuditRepository
{
    void Add(AdminAuditEntry entry);

    IReadOnlyCollection<AdminAuditEntry> List(int limit);
}
