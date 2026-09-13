using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 真实 PostgreSQL 回归测试的入口：解析连接串、探测可用性，并据此决定用例是跑还是跳过。
///
/// 为什么需要这一层：本仓库已经出现过三类**只有真机才暴露**的缺陷——
/// 乐观并发令牌因为 <c>AsNoTracking</c> 形同虚设、EF 导航集合让新消息生成 UPDATE 而触发并发异常、
/// <c>EfTaskRepository.Save</c> 逐列复制漏掉草稿正文列（内存仓储保存的是同一个对象引用，所以看不出来）。
/// 这些用例跑在真实库上，才拦得住同类问题。
///
/// 连接串来源（按顺序）：环境变量 <c>AITOHUMAN_TEST_POSTGRES</c> → 本机
/// <c>backend/AIToHuman.Api/appsettings.Development.json</c>（该文件不进 Git，凭据不会进仓库）。
/// 两者都拿不到、或库连不上时，标记为不可用，用例整体跳过而不是失败——
/// 这样没有数据库的机器与 CI 仍然能跑完其余测试。
/// </summary>
public static class PostgresTestEnvironment
{
    /// <summary>覆盖连接串的环境变量名（CI 里用 services.postgres 注入）。</summary>
    public const string ConnectionStringVariable = "AITOHUMAN_TEST_POSTGRES";

    private static readonly Lazy<string?> Probe = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>能不能连上真实 PostgreSQL；解析结果只探测一次。</summary>
    public static bool Available => Probe.Value is not null;

    /// <summary>维护库（通常是 postgres）的连接串；测试库由它派生。</summary>
    public static string? MaintenanceConnectionString => Probe.Value;

    /// <summary>不可用时的原因，写进跳过信息里，方便排查。</summary>
    public static string SkipReason { get; private set; } = "未配置测试用 PostgreSQL。";

    private static string? Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        var source = ConnectionStringVariable;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ReadFromDevelopmentSettings();
            source = "appsettings.Development.json";
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            SkipReason = $"没有找到 PostgreSQL 连接串（环境变量 {ConnectionStringVariable} 或 backend/AIToHuman.Api/appsettings.Development.json），真实数据库用例已跳过。";
            return null;
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(configured) { Timeout = 3, CommandTimeout = 15 };
            using var connection = new NpgsqlConnection(builder.ConnectionString);
            connection.Open();
            return builder.ConnectionString;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or ArgumentException)
        {
            SkipReason = $"无法连接测试用 PostgreSQL（来源：{source}）：{exception.Message}。真实数据库用例已跳过。";
            return null;
        }
    }

    /// <summary>从 API 的本地开发配置里读连接串：它被 Git 忽略，凭据不会进仓库。</summary>
    private static string? ReadFromDevelopmentSettings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "AIToHuman.Api", "appsettings.Development.json");
            if (File.Exists(candidate)) return ExtractPostgres(candidate);
            directory = directory.Parent;
        }

        return null;
    }

    private static string? ExtractPostgres(string path)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("ConnectionStrings", out var connections)
                && connections.TryGetProperty("Postgres", out var postgres)
                ? postgres.GetString()
                : null;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// 需要真实 PostgreSQL 的用例。数据库不可用时把用例标记为跳过（而不是失败），
/// 因此没有装数据库的机器和默认 CI 仍然能跑完其余测试。
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestEnvironment.Available) Skip = PostgresTestEnvironment.SkipReason;
    }
}
