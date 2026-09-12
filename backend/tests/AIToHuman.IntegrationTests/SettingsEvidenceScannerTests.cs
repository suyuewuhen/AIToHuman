using System.Net;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 扫描实现的选择：none 显式放行、http 调外部服务、clamav 走 clamd，未知取值也按放行处理并打警告。
/// </summary>
public sealed class SettingsEvidenceScannerTests
{
    private static readonly byte[] Payload = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01];

    [Fact]
    public async Task Provider_none_allows_content_without_calling_anything()
    {
        var recorder = new RecordingHttpHandler(_ => throw new InvalidOperationException("默认配置下不应该发起扫描请求"));
        var scanner = Build(recorder, new Dictionary<string, string>());

        Assert.Equal(EvidenceScanStatus.Clean, await scanner.ScanAsync("order/file.png", "image/png"));
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Unknown_provider_is_treated_as_not_configured()
    {
        var recorder = new RecordingHttpHandler(_ => throw new InvalidOperationException("未知取值不应该走 http"));
        var scanner = Build(recorder, new Dictionary<string, string> { [SettingKeys.EvidenceScannerProvider] = "vendor-x" });

        Assert.Equal(EvidenceScanStatus.Clean, await scanner.ScanAsync("order/file.png", "image/png"));
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Provider_http_sends_the_content_to_the_configured_service()
    {
        var recorder = new RecordingHttpHandler(_ => SettingsTestData.Json("{\"verdict\":\"clean\"}"));
        var scanner = Build(recorder, new Dictionary<string, string>
        {
            [SettingKeys.EvidenceScannerProvider] = "http",
            [SettingKeys.EvidenceScannerEndpoint] = "https://scanner.example.com/scan"
        });

        Assert.Equal(EvidenceScanStatus.Clean, await scanner.ScanAsync("order/file.png", "image/png"));
        var request = Assert.Single(recorder.Requests);
        Assert.Equal("https://scanner.example.com/scan", request.Url);
        Assert.Equal(Payload, request.Body);
    }

    [Fact]
    public async Task Provider_clamav_goes_to_clamd_instead_of_http()
    {
        var recorder = new RecordingHttpHandler(_ => throw new InvalidOperationException("clamav 模式不应该走 http"));
        // 指向一个没人监听的端口 + failMode=open：既能证明走了 clamd 分支，又不用真的部署 clamd。
        var scanner = Build(recorder, new Dictionary<string, string>
        {
            [SettingKeys.EvidenceScannerProvider] = "clamav",
            [SettingKeys.EvidenceScannerClamAvHost] = "127.0.0.1",
            [SettingKeys.EvidenceScannerClamAvPort] = FreePort().ToString(),
            [SettingKeys.EvidenceScannerFailMode] = "open"
        });

        Assert.Equal(EvidenceScanStatus.Clean, await scanner.ScanAsync("order/file.png", "image/png"));
        Assert.Empty(recorder.Requests);
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static SettingsEvidenceScanner Build(RecordingHttpHandler handler, Dictionary<string, string> values)
    {
        var storage = new InMemoryFileStorage();
        storage.Files["order/file.png"] = Payload;
        var settings = new StubSettingsProvider(values);

        return new SettingsEvidenceScanner(
            settings,
            new HttpEvidenceScanner(
                settings,
                storage,
                new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
                NullLogger<HttpEvidenceScanner>.Instance),
            new ClamAvEvidenceScanner(settings, storage, NullLogger<ClamAvEvidenceScanner>.Instance),
            NullLogger<SettingsEvidenceScanner>.Instance);
    }
}
