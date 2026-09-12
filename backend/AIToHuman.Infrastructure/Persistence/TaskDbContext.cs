using AIToHuman.Domain.Admin;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Persistence;

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();
    public DbSet<ApplicationRecord> Applications => Set<ApplicationRecord>();
    public DbSet<UserRecord> Users => Set<UserRecord>();
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();
    public DbSet<ReviewRecord> Reviews => Set<ReviewRecord>();
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();
    public DbSet<ConversationMessageRecord> ConversationMessages => Set<ConversationMessageRecord>();
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();
    public DbSet<OrderMessageRecord> OrderMessages => Set<OrderMessageRecord>();
    public DbSet<EvidenceRecord> Evidence => Set<EvidenceRecord>();
    public DbSet<SystemSettingRecord> SystemSettings => Set<SystemSettingRecord>();
    public DbSet<SettingsAuditRecord> SettingsAudits => Set<SettingsAuditRecord>();
    public DbSet<AdminAuditRecord> AdminAudits => Set<AdminAuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.ToTable("tasks");
            entity.HasKey(item => item.Id);
            // 乐观并发令牌：Save 时自增，EF 用原值做 WHERE；并发改价或并发选人时后写入者会拿到 DbUpdateConcurrencyException。
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.Property(item => item.RewardAmount).HasPrecision(18, 2);
            entity.Property(item => item.ExecutionAddress).HasMaxLength(200);
            entity.Property(item => item.RewardCurrency).HasMaxLength(3).IsRequired();
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.AcceptanceCriteriaJson).IsRequired();
            entity.HasIndex(item => new { item.Status, item.Deadline });
            entity.HasMany(item => item.Applications).WithOne(item => item.Task).HasForeignKey(item => item.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApplicationRecord>(entity =>
        {
            entity.ToTable("task_applications");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(item => item.Note).HasMaxLength(1000);
            entity.HasIndex(item => new { item.TaskId, item.WorkerId, item.Status });
        });

        modelBuilder.Entity<UserRecord>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Email).IsUnique();
            entity.Property(item => item.Email).HasMaxLength(320).IsRequired();
            entity.Property(item => item.DisplayName).HasMaxLength(80).IsRequired();
            entity.Property(item => item.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(item => item.Role).HasMaxLength(16).IsRequired();
            entity.Property(item => item.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<OrderRecord>(entity =>
        {
            entity.ToTable("orders");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Version).IsConcurrencyToken();
            entity.HasIndex(item => item.TaskId).IsUnique();
            entity.Property(item => item.Title).HasMaxLength(80).IsRequired();
            entity.Property(item => item.RewardAmount).HasPrecision(18, 2);
            entity.Property(item => item.RewardCurrency).HasMaxLength(3).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
            entity.Property(item => item.EvidenceNote).HasMaxLength(4000);
            entity.Property(item => item.ReviewNote).HasMaxLength(4000);
            entity.Property(item => item.RejectionNote).HasMaxLength(4000);
        });

        modelBuilder.Entity<ReviewRecord>(entity =>
        {
            entity.ToTable("reviews");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.OrderId, item.ReviewerId }).IsUnique();
            entity.HasIndex(item => item.RevieweeId);
            entity.Property(item => item.Comment).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<ConversationRecord>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.UserId, item.UpdatedAt });
            entity.HasMany(item => item.Messages).WithOne().HasForeignKey(item => item.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationMessageRecord>(entity =>
        {
            entity.ToTable("conversation_messages");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.ConversationId, item.Sequence }).IsUnique();
            entity.Property(item => item.Role).HasMaxLength(16).IsRequired();
            entity.Property(item => item.Content).HasMaxLength(4000).IsRequired();
            entity.Property(item => item.PlanJson).HasMaxLength(8000);
        });

        modelBuilder.Entity<NotificationRecord>(entity =>
        {
            entity.ToTable("notifications");
            entity.HasKey(item => item.Id);
            // 幂等键：同一个业务事件只允许一条通知。
            entity.HasIndex(item => item.EventId).IsUnique();
            entity.HasIndex(item => new { item.UserId, item.ReadAt });
            // Outbox：派发任务按未派发 + 创建时间扫描。
            entity.HasIndex(item => item.DispatchedAt);
            entity.Property(item => item.Type).HasMaxLength(80).IsRequired();
            entity.Property(item => item.PayloadJson).HasMaxLength(8000).IsRequired();
        });

        modelBuilder.Entity<OrderMessageRecord>(entity =>
        {
            entity.ToTable("order_messages");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.OrderId, item.CreatedAt });
            entity.Property(item => item.Content).HasMaxLength(2000).IsRequired();
        });

        modelBuilder.Entity<EvidenceRecord>(entity =>
        {
            entity.ToTable("evidence");
            entity.HasKey(item => item.Id);
            // 存储键由系统生成且全局唯一，作为对象存储里的定位键。
            entity.HasIndex(item => item.StorageKey).IsUnique();
            entity.HasIndex(item => new { item.OrderId, item.CreatedAt });
            entity.Property(item => item.FileName).HasMaxLength(200).IsRequired();
            entity.Property(item => item.ContentType).HasMaxLength(100).IsRequired();
            entity.Property(item => item.StorageKey).HasMaxLength(120).IsRequired();
            entity.Property(item => item.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(item => item.ScanStatus).HasMaxLength(16).IsRequired();
            entity.Property(item => item.LastScanNote).HasMaxLength(OrderEvidence.MaxScanNoteLength);
            entity.Property(item => item.MetadataRemoved).HasMaxLength(OrderEvidence.MaxMetadataNoteLength);
            // 按上传者限速时要统计“某人最近一小时的提交”，这里给它一条索引。
            entity.HasIndex(item => new { item.UploadedBy, item.CreatedAt });
            // 后台重扫按“待扫描 + 上传时间”扫描，这里给它一条索引。
            entity.HasIndex(item => new { item.ScanStatus, item.CreatedAt });
        });

        modelBuilder.Entity<SystemSettingRecord>(entity =>
        {
            entity.ToTable("system_settings");
            entity.HasKey(item => item.Key);
            entity.Property(item => item.Key).HasMaxLength(SystemSetting.MaxKeyLength).IsRequired();
            entity.Property(item => item.Value).HasMaxLength(SystemSetting.MaxValueLength).IsRequired();
            // 乐观并发令牌：两个管理员同时保存同一条配置时，后写入者拿到 DbUpdateConcurrencyException（409）。
            entity.Property(item => item.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<SettingsAuditRecord>(entity =>
        {
            entity.ToTable("system_setting_audits");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => new { item.Key, item.OccurredAt });
            entity.Property(item => item.Key).HasMaxLength(SystemSetting.MaxKeyLength).IsRequired();
            entity.Property(item => item.Action).HasMaxLength(16).IsRequired();
            entity.Property(item => item.OldValue).HasMaxLength(SettingsAuditEntry.MaxValueLength).IsRequired();
            entity.Property(item => item.NewValue).HasMaxLength(SettingsAuditEntry.MaxValueLength).IsRequired();
        });

        modelBuilder.Entity<AdminAuditRecord>(entity =>
        {
            entity.ToTable("admin_audit_entries");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.OccurredAt);
            entity.HasIndex(item => new { item.TargetType, item.TargetId });
            entity.Property(item => item.Action).HasMaxLength(AdminAuditEntry.MaxActionLength).IsRequired();
            entity.Property(item => item.TargetType).HasMaxLength(AdminAuditEntry.MaxTargetTypeLength).IsRequired();
            entity.Property(item => item.Reason).HasMaxLength(AdminAuditEntry.MaxReasonLength).IsRequired();
        });
    }
}

public sealed class TaskRecord
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string District { get; set; } = "";

    /// <summary>精确执行地址，只在订单成立后向参与者披露。</summary>
    public string? ExecutionAddress { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public decimal RewardAmount { get; set; }
    public string RewardCurrency { get; set; } = "CNY";
    public string Status { get; set; } = "ReadyToPublish";
    public string AcceptanceCriteriaJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; }
    public List<ApplicationRecord> Applications { get; set; } = [];
}

public sealed class ApplicationRecord
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public TaskRecord? Task { get; set; }
    public Guid WorkerId { get; set; }
    public string Note { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string Status { get; set; } = "Pending";
}

public sealed class UserRecord
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "worker";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OrderRecord
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid WorkerId { get; set; }
    public string Title { get; set; } = "";
    public decimal RewardAmount { get; set; }
    public string RewardCurrency { get; set; } = "CNY";
    public string Status { get; set; } = "Accepted";
    public DateTimeOffset CreatedAt { get; set; }
    public string? EvidenceNote { get; set; }
    public string? ReviewNote { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? RejectionNote { get; set; }
    public int ReworkCount { get; set; }
    public int Version { get; set; }
}

public sealed class ReviewRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid ReviewerId { get; set; }
    public Guid RevieweeId { get; set; }
    public int Rating { get; set; }
    public string Comment { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ConversationRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ConversationMessageRecord> Messages { get; set; } = [];
}

public sealed class ConversationMessageRecord
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public int Sequence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool ReadyToDraft { get; set; }
    public string? PlanJson { get; set; }
}

/// <summary>持久化通知，同时充当 Outbox 记录。</summary>
public sealed class NotificationRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EventId { get; set; }
    public string Type { get; set; } = "";
    public int Version { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>订单会话消息。</summary>
public sealed class OrderMessageRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid SenderId { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>执行凭证元数据；文件内容在私有对象存储里，用 <see cref="StorageKey"/> 定位。</summary>
public sealed class EvidenceRecord
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid UploadedBy { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string ScanStatus { get; set; } = "Pending";
    public DateTimeOffset? ScannedAt { get; set; }

    /// <summary>已经尝试过几次扫描（含自动重试）。</summary>
    public int ScanAttempts { get; set; }

    /// <summary>最近一次扫描的说明：通过、拒绝原因，或扫描服务不可用。</summary>
    public string? LastScanNote { get; set; }

    public DateTimeOffset? LastScanAttemptAt { get; set; }

    /// <summary>上传时被剥离的元数据（例如 EXIF/XMP、PNG 文本块）。</summary>
    public string? MetadataRemoved { get; set; }
}

/// <summary>运营可配置项的覆盖值：等于“这条键被后台显式改过”，删除即恢复默认。机密在 <see cref="Value"/> 里是密文。</summary>
public sealed class SystemSettingRecord
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public int Version { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>运营操作审计，只追加；取值已由领域层校验长度。</summary>
public sealed class AdminAuditRecord
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public Guid TargetId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>配置变更审计，只追加；取值已由应用层脱敏。</summary>
public sealed class SettingsAuditRecord
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Action { get; set; } = "Update";
    public string OldValue { get; set; } = "";
    public string NewValue { get; set; } = "";
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
