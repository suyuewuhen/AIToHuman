using System.Net;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

public sealed class HttpEvidenceScannerTests
{
    private static readonly byte[] Content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public async Task Provider_none_allows_content_without_touching_the_network()
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{}"));
        var host = Build(handler, new Dictionary<string, string?>(), InMemoryStorage());

        var status = await host.Scanner.ScanAsync("order/file.png", "image/png");

        Assert.Equal(EvidenceScanStatus.Clean, status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Http_provider_posts_the_file_with_api_key_and_returns_clean()
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"verdict\":\"clean\"}"));
        var host = Build(handler, HttpConfiguration(), InMemoryStorage());

        var status = await host.Scanner.ScanAsync("order/file.png", "image/png");

        Assert.Equal(EvidenceScanStatus.Clean, status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://scanner.example.com/scan", request.Url);
        Assert.Equal("scanner-key", request.ApiKey);
        Assert.Equal("order/file.png", request.StorageKey);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal(Content, request.Body);
    }

    [Fact]
    public async Task Rejected_verdict_is_reported()
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"verdict\":\"rejected\",\"reason\":\"EICAR\"}"));
        var host = Build(handler, HttpConfiguration(), InMemoryStorage());

        Assert.Equal(EvidenceScanStatus.Rejected, await host.Scanner.ScanAsync("order/file.png", "image/png"));
    }

    [Fact]
    public async Task Pending_verdict_is_reported()
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"verdict\":\"pending\"}"));
        var host = Build(handler, HttpConfiguration(), InMemoryStorage());

        Assert.Equal(EvidenceScanStatus.Pending, await host.Scanner.ScanAsync("order/file.png", "image/png"));
    }

    [Fact]
    public async Task Failure_mode_closed_keeps_the_file_pending_instead_of_losing_it()
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"error\":\"boom\"}", HttpStatusCode.InternalServerError));
        var host = Build(handler, HttpConfiguration(), InMemoryStorage());

        var status = await host.Scanner.ScanAsync("order/file.png", "image/png");

        Assert.Equal(EvidenceScanStatus.Pending, status);
    }

    [Fact]
    public async Task Failure_mode_open_allows_content()
    {
        var configuration = HttpConfiguration();
        configuration["Settings:evidence:scanner:failMode"] = "open";
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"error\":\"boom\"}", HttpStatusCode.ServiceUnavailable));
        var host = Build(handler, configuration, InMemoryStorage());

        Assert.Equal(EvidenceScanStatus.Clean, await host.Scanner.ScanAsync("order/file.png", "image/png"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"verdict\":\"maybe\"}")]
    [InlineData("{\"result\":\"clean\"}")]
    public async Task Unusable_responses_follow_the_failure_mode(string body)
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json(body));
        var host = Build(handler, HttpConfiguration(), InMemoryStorage());

        Assert.Equal(EvidenceScanStatus.Pending, await host.Scanner.ScanAsync("order/file.png", "image/png"));
    }

    [Fact]
    public async Task Transport_failure_follows_the_failure_mode()
    {
        var handler = new RecordingHttpHandler(_ => throw new HttpRequestException("dns failure"));
        var host = Build(handler, HttpConfiguration(), InMemoryStorage());

        Assert.Equal(EvidenceScanStatus.Pending, await host.Scanner.ScanAsync("order/file.png", "image/png"));
    }

    [Fact]
    public async Task Enabled_but_unconfigured_endpoint_is_treated_as_unavailable()
    {
        var handler = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"verdict\":\"clean\"}"));
        var configuration = HttpConfiguration();
        configuration["Settings:evidence:scanner:endpoint"] = "";
        var host = Build(handler, configuration, InMemoryStorage());

        Assert.Equal(EvidenceScanStatus.Pending, await host.Scanner.ScanAsync("order/file.png", "image/png"));
        Assert.Empty(handler.Requests);
    }

    private static Dictionary<string, string?> HttpConfiguration() => new()
    {
        ["Settings:evidence:scanner:provider"] = "http",
        ["Settings:evidence:scanner:endpoint"] = "https://scanner.example.com/scan",
        ["Settings:evidence:scanner:apiKey"] = "scanner-key",
        ["Settings:evidence:scanner:timeoutSeconds"] = "5",
        ["Settings:evidence:scanner:failMode"] = "closed"
    };

    private static InMemoryFileStorage InMemoryStorage()
    {
        var storage = new InMemoryFileStorage();
        storage.Files["order/file.png"] = Content;
        return storage;
    }

    /// <summary>用真实的快照与设置提供者组装扫描器，只替换 HTTP 与存储。</summary>
    private static (HttpEvidenceScanner Scanner, InMemoryFileStorage Storage) Build(
        RecordingHttpHandler handler,
        Dictionary<string, string?> configuration,
        InMemoryFileStorage storage)
    {
        var host = new TestSettingsHost(configuration);
        var scanner = new HttpEvidenceScanner(
            host.Provider,
            storage,
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            NullLogger<HttpEvidenceScanner>.Instance);
        return (scanner, storage);
    }
}
