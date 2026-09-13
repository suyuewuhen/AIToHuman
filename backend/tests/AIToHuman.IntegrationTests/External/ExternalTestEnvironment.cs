using AIToHuman.Application.Settings;
using AIToHuman.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 外部依赖（Redis、S3 兼容对象存储）的探测与连接信息。
///
/// 与 PostgreSQL 那套同样的取舍：**能用就跑、不能用就跳过**，绝不让"本机没装 Redis/MinIO"变成红色构建。
/// 配置来源：环境变量优先，其次是本机开发默认值（见下）。
/// </summary>
public static class ExternalTestEnvironment
{
    public const string RedisVariable = "AITOHUMAN_TEST_REDIS";
    public const string S3EndpointVariable = "AITOHUMAN_TEST_S3_ENDPOINT";
    public const string S3RegionVariable = "AITOHUMAN_TEST_S3_REGION";
    public const string S3BucketVariable = "AITOHUMAN_TEST_S3_BUCKET";
    public const string S3AccessKeyVariable = "AITOHUMAN_TEST_S3_ACCESS_KEY";
    public const string S3SecretKeyVariable = "AITOHUMAN_TEST_S3_SECRET_KEY";

    private static readonly Lazy<string?> RedisProbe = new(ResolveRedis, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<S3Probe> S3ProbeResult = new(ResolveS3, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>探测到的 Redis 连接串；不可用时为 null。</summary>
    public static string? RedisConnectionString => RedisProbe.Value;

    /// <summary>能不能连上 Redis。</summary>
    public static bool RedisAvailable => RedisProbe.Value is not null;

    public static string RedisSkipReason { get; private set; } = "未配置测试用 Redis。";

    /// <summary>探测到的对象存储配置；不可用时为 null。</summary>
    public static TestS3Options? S3 => S3ProbeResult.Value.Options;

    /// <summary>对象存储是否可写。</summary>
    public static bool S3Available => S3ProbeResult.Value.Options is not null;

    public static string S3SkipReason { get; private set; } = "未配置测试用对象存储。";

    private static string? ResolveRedis()
    {
        // 与本机开发环境一致：Redis 默认就是 127.0.0.1:6379。
        var connectionString = Environment.GetEnvironmentVariable(RedisVariable);
        if (string.IsNullOrWhiteSpace(connectionString)) connectionString = "127.0.0.1:6379";

        try
        {
            var options = ConfigurationOptions.Parse(connectionString);
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;
            using var connection = ConnectionMultiplexer.Connect(options);
            connection.GetDatabase().Ping();
            return connectionString;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or FormatException)
        {
            RedisSkipReason = $"连不上测试用 Redis（{connectionString}）：{exception.Message}。依赖 Redis 的用例已跳过。";
            return null;
        }
    }

    private static S3Probe ResolveS3()
    {
        var endpoint = Environment.GetEnvironmentVariable(S3EndpointVariable) ?? "http://127.0.0.1:9000";
        var options = new TestS3Options(
            endpoint,
            Environment.GetEnvironmentVariable(S3RegionVariable) ?? "us-east-1",
            // 桶名沿用本机 S3 联调时用的那个；没有这个桶时下面的写探针会失败并让用例跳过。
            Environment.GetEnvironmentVariable(S3BucketVariable) ?? "aitohuman-evidence",
            // 本机 MinIO 的默认凭据只作为本地回归的兜底，CI/其他环境用环境变量注入。
            Environment.GetEnvironmentVariable(S3AccessKeyVariable) ?? "minioadmin",
            Environment.GetEnvironmentVariable(S3SecretKeyVariable) ?? "minioadmin");

        // 写一条探针对象来确认"这个桶真的能写"：桶不存在、密钥不对、服务没起来都会在这里暴露，
        // 而不是让每个用例各自失败（那样看起来像代码坏了，其实是环境没准备好）。
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var storage = CreateStorage(options, client);
            using var payload = new MemoryStream("probe"u8.ToArray());
            storage.SaveAsync(TestS3Options.ProbeKey, payload, "text/plain").GetAwaiter().GetResult();
            storage.DeleteAsync(TestS3Options.ProbeKey).GetAwaiter().GetResult();
            return new S3Probe(options, null);
        }
        catch (Exception exception)
        {
            var reason = $"{endpoint} 上的桶 {options.Bucket} 不可写：{exception.Message}。依赖对象存储的用例已跳过。";
            S3SkipReason = reason;
            return new S3Probe(null, reason);
        }
    }

    /// <summary>按测试配置拼一个 S3FileStorage（与生产同一份实现，只是设置来源不同）。</summary>
    public static S3FileStorage CreateStorage(TestS3Options options, HttpClient client, TimeProvider? timeProvider = null) =>
        new(new TestSettingsProvider(new Dictionary<string, string?>
        {
            [SettingKeys.StorageS3Endpoint] = options.Endpoint,
            [SettingKeys.StorageS3Region] = options.Region,
            [SettingKeys.StorageS3Bucket] = options.Bucket,
            [SettingKeys.StorageS3AccessKeyId] = options.AccessKeyId,
            [SettingKeys.StorageS3SecretAccessKey] = options.SecretAccessKey
        }),
        client,
        timeProvider ?? TimeProvider.System,
        NullLogger<S3FileStorage>.Instance);

    private sealed record S3Probe(TestS3Options? Options, string? Reason);
}

/// <summary>测试用的对象存储参数。</summary>
public sealed record TestS3Options(string Endpoint, string Region, string Bucket, string AccessKeyId, string SecretAccessKey)
{
    /// <summary>探测用的键：每个用例都用自己的键，互不干扰。</summary>
    public const string ProbeKey = "tests/probe.txt";
}

/// <summary>需要一个可用 Redis 的用例；不可用时标记跳过。</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!ExternalTestEnvironment.RedisAvailable) Skip = ExternalTestEnvironment.RedisSkipReason;
    }
}

/// <summary>需要一个可写对象存储的用例；不可用时标记跳过。</summary>
public sealed class MinioFactAttribute : FactAttribute
{
    public MinioFactAttribute()
    {
        if (!ExternalTestEnvironment.S3Available) Skip = ExternalTestEnvironment.S3SkipReason;
    }
}

/// <summary>字典版设置提供者：让基础设施实现能拿到测试指定的配置。</summary>
public sealed class TestSettingsProvider(IReadOnlyDictionary<string, string?> values) : ISettingsProvider
{
    public string? GetValue(string key) => values.TryGetValue(key, out var value) ? value : null;

    public SettingSource GetSource(string key) => values.ContainsKey(key) ? SettingSource.Configuration : SettingSource.Default;
}
