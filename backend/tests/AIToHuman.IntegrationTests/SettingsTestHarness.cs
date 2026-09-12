using System.Net;
using AIToHuman.Application.Settings;
using AIToHuman.Api.Settings;
using AIToHuman.Contracts.Settings;
using AIToHuman.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>机密保护的替身：加前缀后反转内容，便于断言“落库的不是明文”。</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private const string Prefix = "fake:";

    /// <summary>置为 true 时模拟密钥环更换：解密一律失败。</summary>
    public bool Broken { get; set; }

    public List<string> Protected { get; } = [];

    public string Protect(string plaintext)
    {
        Protected.Add(plaintext);
        return Prefix + new string(plaintext.Reverse().ToArray());
    }

    public string? Unprotect(string stored)
    {
        if (Broken) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        return new string(stored[Prefix.Length..].Reverse().ToArray());
    }
}

internal sealed class StubProbe(SettingTestResponse response) : ISettingProbe
{
    public List<string> Keys { get; } = [];

    public Task<SettingTestResponse> ProbeAsync(string key, CancellationToken cancellationToken = default)
    {
        Keys.Add(key);
        return Task.FromResult(response with { Key = key });
    }
}

/// <summary>可变的配置提供者，用于验证消费方每次调用都重新读取配置。</summary>
internal sealed class MutableSettingsProvider : ISettingsProvider
{
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

    public void Set(string key, string value) => values[key] = value;

    public string? GetValue(string key) => values.GetValueOrDefault(key);

    public SettingSource GetSource(string key) => values.ContainsKey(key) ? SettingSource.Configuration : SettingSource.Default;
}

/// <summary>刷新快照的替身：和生产的 SettingsSnapshotReloader 行为一致，只是不经过 DI 作用域。</summary>
internal sealed class SnapshotReloader(SettingsSnapshotBuilder builder, SettingsCache cache) : ISettingsReloader
{
    public int Count { get; private set; }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Count++;
        cache.Replace(builder.Build());
        return Task.CompletedTask;
    }
}

/// <summary>记录请求并返回预设响应的 HTTP 替身。</summary>
internal sealed class RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Requests.Add(new RecordedRequest(
            request.RequestUri?.ToString(),
            request.Headers.TryGetValues("X-Api-Key", out var apiKey) ? string.Join(",", apiKey) : null,
            request.Headers.TryGetValues("X-Evidence-Key", out var storageKey) ? string.Join(",", storageKey) : null,
            request.Content?.Headers.ContentType?.MediaType,
            body));

        return responder(request);
    }

    internal sealed record RecordedRequest(string? Url, string? ApiKey, string? StorageKey, string? ContentType, byte[] Body);
}

/// <summary>
/// 测试用的配置环境：内存仓储 + 真实快照构建 + 真实 SettingsService，
/// 只把机密保护与自检换成替身，其他链路与生产完全一致。
/// </summary>
internal sealed class TestSettingsHost
{
    public TestSettingsHost(Dictionary<string, string?>? configuration = null, ISettingProbe? probe = null)
    {
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(configuration ?? []).Build();
        Provider = new DatabaseSettingsProvider(Cache);
        builder = new SettingsSnapshotBuilder(Repository, Configuration, Protector, NullLogger<SettingsSnapshotBuilder>.Instance);
        Cache.Replace(builder.Build());
        Service = new SettingsService(Repository, Provider, Protector, new SnapshotReloader(builder, Cache), probe ?? new StubProbe(new SettingTestResponse(string.Empty, true, "ok")), Clock);
    }

    private readonly SettingsSnapshotBuilder builder;

    public InMemorySystemSettingRepository Repository { get; } = new();

    public FakeSecretProtector Protector { get; } = new();

    public SettingsCache Cache { get; } = new();

    public IConfigurationRoot Configuration { get; }

    public DatabaseSettingsProvider Provider { get; }

    public SettingsService Service { get; }

    public MutableTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));

    /// <summary>模拟后台轮询或另一个实例改库之后重新加载快照。</summary>
    public void Reload() => Cache.Replace(builder.Build());

    public SettingsAuditEntryView[] Audits(int limit = 20) =>
        Service.Audits(limit).Select(item => new SettingsAuditEntryView(item.Key, item.Action, item.OldValue, item.NewValue, item.ActorId)).ToArray();

    internal sealed record SettingsAuditEntryView(string Key, string Action, string OldValue, string NewValue, Guid ActorId);
}

internal static class SettingsTestData
{
    public static readonly Guid Admin = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static HttpResponseMessage Json(string body, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
}
