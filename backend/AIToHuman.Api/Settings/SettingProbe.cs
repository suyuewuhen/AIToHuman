using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Settings;
using AIToHuman.Infrastructure.Storage;

namespace AIToHuman.Api.Settings;

/// <summary>
/// “测试连接”的只读自检：本机目录能不能写、模型服务与扫描服务能不能连通。
/// 只做读操作与一次临时文件写入，不动任何业务数据。
/// </summary>
public sealed class SettingProbe(
    ISettingsProvider settings,
    IHttpClientFactory httpClientFactory,
    LocalFileStorage localStorage,
    ILogger<SettingProbe> logger) : ISettingProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    public async Task<SettingTestResponse> ProbeAsync(string key, CancellationToken cancellationToken = default) => key switch
    {
        SettingKeys.StorageProvider or SettingKeys.StorageLocalRoot => ProbeLocalStorage(key),
        SettingKeys.AiProvider or SettingKeys.AiBaseUrl or SettingKeys.AiApiKey or SettingKeys.AiModel => await ProbeAiAsync(key, cancellationToken),
        SettingKeys.EvidenceScannerProvider or SettingKeys.EvidenceScannerEndpoint or SettingKeys.EvidenceScannerApiKey => await ProbeScannerAsync(key, cancellationToken),
        SettingKeys.EvidenceScannerFailMode or SettingKeys.EvidenceScannerTimeoutSeconds =>
            new SettingTestResponse(key, true, "该设置只影响扫描不可用时的兜底行为，保存后立即生效。"),
        _ => new SettingTestResponse(key, true, "该设置没有自检项，保存后立即生效。")
    };

    private SettingTestResponse ProbeLocalStorage(string key)
    {
        var provider = settings.GetChoice(SettingKeys.StorageProvider, "local");
        if (provider != "local")
            return new SettingTestResponse(key, false, $"当前 storage.provider={provider}，本机目录不会被使用；该类型的存储实现尚未接入。");

        try
        {
            var root = localStorage.Root;
            Directory.CreateDirectory(root);
            var probeFile = Path.Combine(root, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            return new SettingTestResponse(key, true, $"本机存储目录可用：{root}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SettingTestResponse(key, false, $"本机存储目录不可用：{exception.Message}");
        }
    }

    private async Task<SettingTestResponse> ProbeAiAsync(string key, CancellationToken cancellationToken)
    {
        var baseUrl = settings.GetValue(SettingKeys.AiBaseUrl);
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new SettingTestResponse(key, false, "还没有配置 AI 接口地址，模型调用会失败。");

        var apiKey = settings.GetValue(SettingKeys.AiApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new SettingTestResponse(key, false, "还没有配置 AI API Key，模型调用会失败。");

        return await SendProbeAsync(key, HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/models", apiKey, "AI 服务", cancellationToken);
    }

    private async Task<SettingTestResponse> ProbeScannerAsync(string key, CancellationToken cancellationToken)
    {
        var provider = settings.GetChoice(SettingKeys.EvidenceScannerProvider, "none");
        if (provider != "http")
            return new SettingTestResponse(key, false, "当前 evidence.scanner.provider=none：凭证会直接放行。生产环境建议切换为 http 并配置扫描服务。");

        var endpoint = settings.GetValue(SettingKeys.EvidenceScannerEndpoint);
        if (string.IsNullOrWhiteSpace(endpoint))
            return new SettingTestResponse(key, false, "provider=http 但还没有配置扫描服务地址，上传会按失败模式处理。");

        return await SendProbeAsync(key, HttpMethod.Post, endpoint, settings.GetValue(SettingKeys.EvidenceScannerApiKey), "内容扫描服务", cancellationToken, body: "{\"probe\":true}");
    }

    private async Task<SettingTestResponse> SendProbeAsync(
        string key,
        HttpMethod method,
        string url,
        string? apiKey,
        string target,
        CancellationToken cancellationToken,
        string? body = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            var client = httpClientFactory.CreateClient("settings-probe");
            using var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
                return new SettingTestResponse(key, true, $"{target}可达且接受了当前密钥（HTTP {(int)response.StatusCode}）。");

            if ((int)response.StatusCode is 401 or 403)
                return new SettingTestResponse(key, false, $"{target}可达，但当前密钥被拒绝（HTTP {(int)response.StatusCode}）。");

            return new SettingTestResponse(key, false, $"{target}返回 HTTP {(int)response.StatusCode}，无法据此确认配置是否正确。");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning(exception, "自检 {Key} 时无法访问 {Target}。", key, target);
            return new SettingTestResponse(key, false, $"无法连接{target}：{exception.Message}");
        }
    }
}
