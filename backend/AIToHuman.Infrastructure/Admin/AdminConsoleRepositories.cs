using System.Text.Json;
using AIToHuman.Application.Admin;
using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
// System.Threading.Tasks 里也有一个 TaskStatus，这里明确用领域里的那个。
using TaskStatus = AIToHuman.Domain.Tasks.TaskStatus;

namespace AIToHuman.Infrastructure.Admin;

/// <summary>
/// 运营后台的跨所有者检索。普通仓储的查询都按所有者过滤，这里刻意放开，因此单独一个接口，
/// 只在需要人工兜底的地方使用。
/// </summary>
public sealed class EfAdminTaskQuery(TaskDbContext db) : IAdminTaskQuery
{
    public IReadOnlyCollection<TaskItem> Search(string? keyword, TaskStatus? status, int limit)
    {
        var query = db.Tasks.AsNoTracking().Include(item => item.Applications).AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(item =>
                EF.Functions.ILike(item.Title, pattern) ||
                EF.Functions.ILike(item.Description, pattern) ||
                EF.Functions.ILike(item.District, pattern));
        }

        if (status is { } expected) query = query.Where(item => item.Status == expected.ToString());

        return query
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Take(limit)
            .AsEnumerable()
            .Select(Map)
            .ToArray();
    }

    private static TaskItem Map(TaskRecord record) => TaskItem.Rehydrate(
        record.Id,
        record.OwnerId,
        record.Title,
        record.Description,
        record.District,
        record.Deadline,
        new Money(record.RewardAmount, record.RewardCurrency),
        JsonSerializer.Deserialize<string[]>(record.AcceptanceCriteriaJson) ?? [],
        record.CreatedAt,
        Enum.TryParse<TaskStatus>(record.Status, out var status) ? status : TaskStatus.ReadyToPublish,
        record.Applications.Select(item => TaskApplication.Rehydrate(
            item.Id,
            item.WorkerId,
            item.Note,
            item.SubmittedAt,
            Enum.TryParse<TaskApplicationStatus>(item.Status, out var applicationStatus) ? applicationStatus : TaskApplicationStatus.Pending)),
        record.ExecutionAddress);
}

/// <summary>用户目录的 PostgreSQL 实现：运营检索用，只读。</summary>
public sealed class EfUserDirectory(TaskDbContext db) : IUserDirectory
{
    public IReadOnlyCollection<AdminUserView> Search(string? keyword, int limit)
    {
        var query = db.Users.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(item => EF.Functions.ILike(item.Email, pattern) || EF.Functions.ILike(item.DisplayName, pattern));
        }

        return query
            .OrderByDescending(item => item.CreatedAt)
            .Take(limit)
            .AsEnumerable()
            .Select(UserDirectoryMapper.Map)
            .ToArray();
    }

    public IReadOnlyDictionary<Guid, AdminUserView> FindMany(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new Dictionary<Guid, AdminUserView>();

        return db.Users
            .AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .AsEnumerable()
            .Select(UserDirectoryMapper.Map)
            .ToDictionary(item => item.Id);
    }
}

/// <summary>没有 PostgreSQL 时（只跑内存仓储的开发/测试路径）用户目录返回空结果。</summary>
public sealed class EmptyUserDirectory : IUserDirectory
{
    public IReadOnlyCollection<AdminUserView> Search(string? keyword, int limit) => [];

    public IReadOnlyDictionary<Guid, AdminUserView> FindMany(IReadOnlyCollection<Guid> ids) => new Dictionary<Guid, AdminUserView>();
}

internal static class UserDirectoryMapper
{
    public static AdminUserView Map(UserRecord record) => new(record.Id, record.Email, record.DisplayName, record.Role, record.CreatedAt);
}

/// <summary>运营审计的 PostgreSQL 实现：只追加、按时间倒序读。</summary>
public sealed class EfAdminAuditRepository(TaskDbContext db) : IAdminAuditRepository
{
    public void Add(AdminAuditEntry entry)
    {
        db.AdminAudits.Add(new AdminAuditRecord
        {
            Id = entry.Id,
            ActorId = entry.ActorId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Reason = entry.Reason,
            OccurredAt = entry.OccurredAt
        });
        db.SaveChanges();
    }

    public IReadOnlyCollection<AdminAuditEntry> List(int limit) => db.AdminAudits
        .AsNoTracking()
        .OrderByDescending(item => item.OccurredAt)
        .ThenByDescending(item => item.Id)
        .Take(limit)
        .AsEnumerable()
        .Select(item => AdminAuditEntry.Rehydrate(item.Id, item.ActorId, item.Action, item.TargetType, item.TargetId, item.Reason, item.OccurredAt))
        .ToArray();
}

/// <summary>运营审计的内存实现（未配置 PostgreSQL 时的回退与测试用）。</summary>
public sealed class InMemoryAdminAuditRepository : IAdminAuditRepository
{
    private readonly List<AdminAuditEntry> entries = [];
    private readonly Lock gate = new();

    public void Add(AdminAuditEntry entry) { lock (gate) { entries.Add(entry); } }

    public IReadOnlyCollection<AdminAuditEntry> List(int limit)
    {
        lock (gate)
        {
            return entries.OrderByDescending(item => item.OccurredAt).ThenByDescending(item => item.Id).Take(limit).ToArray();
        }
    }
}
