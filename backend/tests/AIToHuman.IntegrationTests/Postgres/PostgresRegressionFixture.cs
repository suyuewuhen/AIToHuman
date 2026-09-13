using AIToHuman.Application.Admin;
using AIToHuman.Application.Common;
using AIToHuman.Application.Conversations;
using AIToHuman.Application.Idempotency;
using AIToHuman.Application.Notifications;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Risk;
using AIToHuman.Application.Settings;
using AIToHuman.Application.Tasks;
using AIToHuman.Contracts.Tasks;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Admin;
using AIToHuman.Infrastructure.Conversations;
using AIToHuman.Infrastructure.Idempotency;
using AIToHuman.Infrastructure.Notifications;
using AIToHuman.Infrastructure.Orders;
using AIToHuman.Infrastructure.Persistence;
using AIToHuman.Infrastructure.Risk;
using AIToHuman.Infrastructure.Settings;
using AIToHuman.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 一次测试运行共用一个真实 PostgreSQL 测试库：建库 + 跑迁移只做一次，
/// 每个用例开始前清表（<see cref="ResetAsync"/>），因此用例之间互不影响，也不需要很大的开销。
/// 测试库名带随机后缀，不会碰到开发库；运行结束尽力删掉它。
/// </summary>
public sealed class PostgresRegressionFixture : IAsyncLifetime
{
    private readonly string databaseName = $"aitohuman_regression_{Guid.NewGuid():N}"[..40];

    /// <summary>测试库连接串；用例代码只把它交给 <see cref="PostgresWorld"/>。</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var maintenance = PostgresTestEnvironment.MaintenanceConnectionString
            ?? throw new InvalidOperationException("没有可用的 PostgreSQL 连接串，不应该构造这个 fixture。");

        ConnectionString = new NpgsqlConnectionStringBuilder(maintenance) { Database = databaseName }.ConnectionString;

        // Migrate() 会负责建库并按顺序应用全部迁移——这一步本身就顺带验证了“空库能不能起来”。
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();

        try
        {
            await using var connection = new NpgsqlConnection(PostgresTestEnvironment.MaintenanceConnectionString);
            await connection.OpenAsync();
            await using (var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name", connection))
            {
                terminate.Parameters.AddWithValue("name", databaseName);
                await terminate.ExecuteNonQueryAsync();
            }

            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", connection);
            await drop.ExecuteNonQueryAsync();
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
        {
            // 删库失败不该让整轮测试变红：留一个带随机后缀的库不影响后续运行。
            Console.WriteLine($"清理测试库 {databaseName} 失败：{exception.Message}");
        }
    }

    /// <summary>清空所有表，让每个用例从干净状态开始。</summary>
    public async Task ResetAsync()
    {
        await using var context = CreateContext();
        var tables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct()
            .Select(name => $"\"{name}\"")
            .ToArray();

        if (tables.Length == 0) return;
        // 表名来自 EF 模型（不是用户输入），这里先拼成普通字符串再执行，避免 SQL 注入分析器误报。
        var sql = $"TRUNCATE TABLE {string.Join(", ", tables)} RESTART IDENTITY CASCADE";
        await context.Database.ExecuteSqlRawAsync(sql);
    }

    /// <summary>当前已应用的迁移；用来断言“空库建出来就是完整 schema”。</summary>
    public async Task<(int Applied, int Pending)> MigrationStateAsync()
    {
        await using var context = CreateContext();
        var applied = (await context.Database.GetAppliedMigrationsAsync()).Count();
        var pending = (await context.Database.GetPendingMigrationsAsync()).Count();
        return (applied, pending);
    }

    public TaskDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<TaskDbContext>().UseNpgsql(ConnectionString).Options);
}

/// <summary>把真实数据库上的用例串到同一个测试库上，并保证它们顺序执行（同一个 collection 不并行）。</summary>
[CollectionDefinition(Name)]
public sealed class PostgresRegressionCollection : ICollectionFixture<PostgresRegressionFixture>
{
    public const string Name = "postgres-regression";
}

/// <summary>
/// 用例侧的"请求作用域"：按 API 的注册方式搭一套 EF 仓储 + 应用服务，
/// 一个实例代表一次请求（同一个 <see cref="TaskDbContext"/>）。
/// 需要模拟并发时用 <see cref="NewScope"/> 拿独立的 DbContext——共用一个上下文是测不出并发问题的。
/// </summary>
public sealed class PostgresWorld : IDisposable
{
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;

    public PostgresWorld(string connectionString, DateTimeOffset now)
    {
        Clock = new MutableTimeProvider(now);
        Owner = Guid.NewGuid();
        Worker = Guid.NewGuid();
        AdminId = Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddDbContext<TaskDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ITaskRepository, EfTaskRepository>();
        services.AddScoped<ITaskRevisionRepository, EfTaskRevisionRepository>();
        services.AddScoped<IOrderRepository, EfOrderRepository>();
        services.AddScoped<IReviewRepository, EfReviewRepository>();
        services.AddScoped<IConversationRepository, EfConversationRepository>();
        services.AddScoped<INotificationRepository, EfNotificationRepository>();
        services.AddScoped<IOrderMessageRepository, EfOrderMessageRepository>();
        services.AddScoped<IEvidenceRepository, EfEvidenceRepository>();
        services.AddScoped<ISystemSettingsRepository, EfSystemSettingRepository>();
        services.AddScoped<IAdminTaskQuery, EfAdminTaskQuery>();
        services.AddScoped<IAdminOrderQuery, EfAdminOrderQuery>();
        services.AddScoped<IUserDirectory, EfUserDirectory>();
        services.AddScoped<IAdminAuditRepository, EfAdminAuditRepository>();
        services.AddScoped<IIdempotencyStore, EfIdempotencyStore>();
        services.AddScoped<IRiskRuleCatalogStore, EfRiskRuleCatalogStore>();
        services.AddScoped<IRiskRuleCatalogProvider, RiskRuleCatalogStoreProvider>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<NotificationService>();
        services.AddScoped<TaskService>();
        services.AddScoped<AdminConsoleService>();
        services.AddScoped<RiskAppealService>();
        services.AddScoped<RiskRuleCatalogService>();
        services.AddScoped<RiskEnforcementService>();

        provider = services.BuildServiceProvider();
        scope = provider.CreateScope();
    }

    /// <summary>固定时钟，让截止时间与判定时刻这类断言可确定。</summary>
    public TimeProvider Clock { get; }
    public Guid Owner { get; }    public Guid Worker { get; }
    public Guid AdminId { get; }

    public IServiceProvider Services => scope.ServiceProvider;
    public TaskService Service => Services.GetRequiredService<TaskService>();
    public AdminConsoleService Admin => Services.GetRequiredService<AdminConsoleService>();
    public RiskAppealService Appeals => Services.GetRequiredService<RiskAppealService>();
    public RiskRuleCatalogService RiskRules => Services.GetRequiredService<RiskRuleCatalogService>();
    public RiskEnforcementService Enforcement => Services.GetRequiredService<RiskEnforcementService>();
    public IRiskRuleCatalogStore RuleStore => Services.GetRequiredService<IRiskRuleCatalogStore>();
    public NotificationService Notifications => Services.GetRequiredService<NotificationService>();
    public ITaskRepository Tasks => Services.GetRequiredService<ITaskRepository>();
    public IOrderRepository Orders => Services.GetRequiredService<IOrderRepository>();

    /// <summary>开一个独立作用域，模拟“另一个请求/另一个实例”——并发用例必须用它。</summary>
    public IServiceScope NewScope() => provider.CreateScope();

    public TaskService ServiceIn(IServiceScope other) => other.ServiceProvider.GetRequiredService<TaskService>();

    public RiskRuleCatalogService RiskRulesIn(IServiceScope other) => other.ServiceProvider.GetRequiredService<RiskRuleCatalogService>();

    public RiskEnforcementService EnforcementIn(IServiceScope other) => other.ServiceProvider.GetRequiredService<RiskEnforcementService>();

    public CreateTaskRequest DraftRequest(
        string title,
        string description = "到前台取件",
        string district = "朝阳区",
        DateTimeOffset? deadline = null,
        decimal reward = 50,
        IReadOnlyList<string>? criteria = null,
        string? executionAddress = null,
        DateTimeOffset? applicationDeadline = null) =>
        new(Owner, title, description, district, deadline ?? Clock.GetUtcNow().AddHours(6), reward, criteria ?? ["按时送达"], executionAddress, applicationDeadline);

    public Guid CreateDraft(string title, decimal reward = 50) => Service.Create(DraftRequest(title, reward: reward)).Id;

    /// <summary>编辑草稿用的请求（字段与创建一致，只是类型不同）。</summary>
    public UpdateTaskDraftRequest DraftUpdateRequest(
        string title,
        string description = "到前台取件",
        string district = "朝阳区",
        DateTimeOffset? deadline = null,
        decimal reward = 50,
        IReadOnlyList<string>? criteria = null,
        string? executionAddress = null,
        DateTimeOffset? applicationDeadline = null) =>
        new(Owner, title, description, district, deadline ?? Clock.GetUtcNow().AddHours(6), reward, criteria ?? ["按时送达"], executionAddress, applicationDeadline);

    public Guid CreatePublished(string title, decimal reward = 50)
    {
        var id = CreateDraft(title, reward);
        Service.Publish(id, Owner);
        return id;
    }

    public Guid Apply(Guid taskId, Guid? workerId = null)
    {
        var applied = Service.Apply(taskId, new ApplyForTaskRequest(workerId ?? Worker, "半小时可到"));
        return applied.Applications.Single(item => item.Status == "Pending").Id;
    }

    public void Dispose()
    {
        scope.Dispose();
        provider.Dispose();
    }
}
