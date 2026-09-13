using System.Diagnostics;
using System.Net.Sockets;
using Npgsql;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 主机级端到端测试的入口：能不能起一个**真实 API 进程**（子进程 + 临时真库 + 真 HTTP）。
///
/// 为什么要有这一层：仓库里已有的测试覆盖了领域规则、用例行为与真库语义，但
/// "路由注册、DI 装配、中间件顺序、JSON 契约、启动期迁移"这一层只有主机级测试才碰得到——
/// 这些地方出错时，单元测试全绿而接口仍然不可用（本项目已经出现过幂等中间件取不到请求头的缺陷）。
///
/// 为什么不用 <c>WebApplicationFactory</c>：它需要 <c>Microsoft.AspNetCore.Mvc.Testing</c> 包，
/// 而当前环境离线取不到（见交接文档第 7 节）。改成**把已构建的 API 当子进程起起来、用真 HTTP 打**，
/// 不依赖任何新包，且比进程内宿主更接近部署形态（进程边界、真实端口、真实信号）。
///
/// 两条门槛都满足才跑，缺任何一条就整组跳过而不是失败：
/// 一是能找到已构建的 <c>AIToHuman.Api.dll</c>（先 <c>dotnet build</c>，CI 里是显式的一步）；
/// 二是能连上测试用 PostgreSQL（与真库回归用例同一个环境变量）。
/// </summary>
public static class HostTestEnvironment
{
    /// <summary>显式关掉主机级用例的开关（只写 "0" 或 "false" 时关闭），便于本机在没有数据库时快速跳过。</summary>
    public const string EnabledVariable = "AITOHUMAN_TEST_HOST";

    private static readonly Lazy<string?> Probe = new(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>能不能跑主机级用例。</summary>
    public static bool Available => Probe.Value is not null;

    /// <summary>已构建的 API 程序集路径（不可用时为 null）。</summary>
    public static string? ApiAssembly => Probe.Value;

    /// <summary>不可用时的原因，写进跳过信息里，方便排查。</summary>
    public static string SkipReason { get; private set; } = "未启用的主机级端到端用例。";

    private static string? Resolve()
    {
        if (Environment.GetEnvironmentVariable(EnabledVariable) is { Length: > 0 } flag
            && (flag == "0" || flag.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            SkipReason = $"环境变量 {EnabledVariable}={flag}，主机级端到端用例已按配置跳过。";
            return null;
        }

        if (!PostgresTestEnvironment.Available)
        {
            SkipReason = $"主机级端到端用例需要真实 PostgreSQL：{PostgresTestEnvironment.SkipReason}";
            return null;
        }

        var assembly = LocateApiAssembly();
        if (assembly is null)
        {
            SkipReason = "没有找到已构建的 AIToHuman.Api.dll：请先执行 dotnet build（主机级用例会真的把 API 起成子进程）。";
            return null;
        }

        return assembly;
    }

    /// <summary>
    /// 从测试程序集的位置往上找仓库根，再按当前配置与框架去 API 的输出目录里找程序集。
    /// 找不到就返回 null（用例跳过），而不是猜一个路径让进程起不来。
    /// </summary>
    private static string? LocateApiAssembly()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "backend", "AIToHuman.Api", "bin", configuration, "net10.0", "AIToHuman.Api.dll");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }
}

/// <summary>
/// 一个真实 API 进程 + 一个只属于它的临时数据库 + 一个空闲端口。
/// 进程用 <c>ASPNETCORE_ENVIRONMENT=Development</c> 起（启动时自动应用迁移），
/// 数据目录与 Data Protection 密钥环都指向临时目录，测完连库一起删掉。
/// </summary>
public sealed class HostE2EFixture : IAsyncLifetime
{
    private readonly string databaseName = $"aitohuman_host_{Guid.NewGuid():N}"[..40];
    private readonly List<string> logs = [];
    private Process? process;
    private string? temporaryRoot;

    /// <summary>API 的基地址（例如 http://127.0.0.1:53211）。</summary>
    public string BaseUrl { get; private set; } = string.Empty;

    /// <summary>临时库连接串（用例需要直接查库断言时用）。</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>进程输出（失败时打印出来，排查不用猜）。</summary>
    public string Log => string.Join(Environment.NewLine, logs);

    public async Task InitializeAsync()
    {
        var maintenance = PostgresTestEnvironment.MaintenanceConnectionString
            ?? throw new InvalidOperationException("没有可用的 PostgreSQL 连接串，不应该构造这个 fixture。");
        var assembly = HostTestEnvironment.ApiAssembly
            ?? throw new InvalidOperationException("没有找到已构建的 API 程序集，不应该构造这个 fixture。");

        ConnectionString = new NpgsqlConnectionStringBuilder(maintenance) { Database = databaseName }.ConnectionString;

        // 数据库不提前建：让应用启动时的 Migrate() 自己建库并按顺序应用全部迁移——
        // 这本身就是"空库能不能起来"的一次真实验证。
        var port = ReserveFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        temporaryRoot = Path.Combine(Path.GetTempPath(), $"aitohuman-host-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);

        var startInfo = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = BaseUrl;
        startInfo.Environment["ConnectionStrings__Postgres"] = ConnectionString;
        // 连接串走环境变量，配置里那份（appsettings.Development.json）不参与，避免误连开发库。
        startInfo.Environment["Admin__Emails"] = "host-e2e-admin@aitohuman.local";
        startInfo.Environment["Admin__UserIds"] = string.Empty;
        startInfo.Environment["DataProtection__KeysPath"] = Path.Combine(temporaryRoot, "keys");
        startInfo.Environment["ObjectStorage__LocalRoot"] = Path.Combine(temporaryRoot, "evidence");
        // 通知扇出需要 Redis，主机级用例不依赖它（默认关闭）。
        startInfo.Environment["Settings__notifications__fanout__enabled"] = "false";

        process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 API 进程。");
        process.OutputDataReceived += (_, args) => { if (args.Data is not null) Append(args.Data); };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Append(args.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForHealthAsync();
    }

    public async Task DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // 进程已经自己退出了。
            }
        }

        process?.Dispose();
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
            Append($"清理主机级测试库 {databaseName} 失败：{exception.Message}");
        }

        if (temporaryRoot is not null && Directory.Exists(temporaryRoot))
        {
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录残留不影响结果。
            }
        }
    }

    /// <summary>就绪探测：只认 /health 返回 healthy，最多等 90 秒（首轮要建库 + 跑 28 个迁移）。</summary>
    private async Task WaitForHealthAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (process is { HasExited: true }) throw new InvalidOperationException($"API 进程启动后立即退出（退出码 {process.ExitCode}）：{Environment.NewLine}{Log}");

            try
            {
                var response = await client.GetAsync($"{BaseUrl}/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // 还没起来：继续等。
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException($"等待 API 就绪超时（90 秒）：{Environment.NewLine}{Log}");
    }

    /// <summary>占一个空闲端口再立刻释放：进程随后绑它，避免写死端口造成并行冲突。</summary>
    private static int ReserveFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void Append(string line)
    {
        lock (logs)
        {
            if (logs.Count < 400) logs.Add(line);
        }
    }
}

/// <summary>主机级端到端用例串到同一个 API 进程上（同一 collection 内顺序执行，不并行抢端口）。</summary>
[CollectionDefinition(Name)]
public sealed class HostE2ECollection : ICollectionFixture<HostE2EFixture>
{
    public const string Name = "host-e2e";
}

/// <summary>需要真实 API 进程的用例；起不来（缺库或缺已构建的程序集）时整条跳过而不是失败。</summary>
public sealed class HostFactAttribute : FactAttribute
{
    public HostFactAttribute()
    {
        if (!HostTestEnvironment.Available) Skip = HostTestEnvironment.SkipReason;
    }
}
