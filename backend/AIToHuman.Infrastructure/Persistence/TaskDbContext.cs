using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Persistence;

public sealed class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();
    public DbSet<ApplicationRecord> Applications => Set<ApplicationRecord>();
    public DbSet<UserRecord> Users => Set<UserRecord>();
    public DbSet<OrderRecord> Orders => Set<OrderRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.ToTable("tasks");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.RewardAmount).HasPrecision(18, 2);
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
            entity.HasIndex(item => item.TaskId).IsUnique();
            entity.Property(item => item.Title).HasMaxLength(80).IsRequired();
            entity.Property(item => item.RewardAmount).HasPrecision(18, 2);
            entity.Property(item => item.RewardCurrency).HasMaxLength(3).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(32).IsRequired();
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
    public DateTimeOffset Deadline { get; set; }
    public decimal RewardAmount { get; set; }
    public string RewardCurrency { get; set; } = "CNY";
    public string Status { get; set; } = "ReadyToPublish";
    public string AcceptanceCriteriaJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
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
}
