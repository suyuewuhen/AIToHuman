using System.Net.Http.Headers;
using System.Text.Json;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace AIToHuman.Infrastructure.Storage;

/// <summary>
/// 按运营配置调用外部内容/病毒扫描服务：
/// <c>evidence.scanner.provider=none</c> 时显式放行并打警告日志；
/// <c>http</c> 时把凭证内容 POST 到 <c>evidence.scanner.endpoint</c>，
/// 期待 <c>{"verdict":"clean|rejected|pending","reason":"..."}</c>。
///
/// 传输失败或响应不可解析时按 <c>evidence.scanner.failMode</c> 处理：
/// <c>closed</c>（默认）返回 <see cref="EvidenceScanStatus.Pending"/> —— 文件保留、不可下载，不丢用户数据；
/// <c>open</c> 直接放行，只建议在开发环境使用。
/// </summary>
public sealed class HttpEvidenceScanner(
    ISettingsProvider settings,
    IFileStorage storage,
    HttpClient httpClient,
    ILogger<HttpEvidenceScanner> logger) : IEvidenceScanner
{
    /// <summary>扫描服务响应体上限，避免对方返回超大内容把内存吃满。</summary>
    private const int MaxResponseBytes = 64 * 1024;

    public async Task<EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        var endpoint = settings.GetValue(SettingKeys.EvidenceScannerEndpoint);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            logger.LogError("内容扫描已启用（provider=http），但没有配置 evidence.scanner.endpoint；{StorageKey} 按失败模式处理。", storageKey);
            return Unavailable();
        }

        var timeoutSeconds = settings.GetInt(SettingKeys.EvidenceScannerTimeoutSeconds) ?? 15;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var content = await storage.OpenReadAsync(storageKey, timeout.Token)
                ?? throw new InvalidOperationException($"凭证文件 {storageKey} 不存在，无法扫描。");

            await using var owned = content;
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StreamContent(owned) };
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType)) request.Content.Headers.ContentType = mediaType;
            request.Headers.TryAddWithoutValidation("X-Evidence-Key", storageKey);
            var apiKey = settings.GetValue(SettingKeys.EvidenceScannerApiKey);
            if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("内容扫描服务返回 {Status}；{StorageKey} 按失败模式处理。", (int)response.StatusCode, storageKey);
                return Unavailable();
            }

            return await ReadVerdictAsync(response, storageKey, timeout.Token) ?? Unavailable();
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            logger.LogError(exception, "调用内容扫描服务失败；{StorageKey} 按失败模式处理。", storageKey);
            return Unavailable();
        }
    }

    private async Task<EvidenceScanStatus?> ReadVerdictAsync(HttpResponseMessage response, string storageKey, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= MaxResponseBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("verdict", out var verdict) || verdict.ValueKind != JsonValueKind.String)
        {
            logger.LogError("内容扫描服务的响应里没有 verdict 字段；{StorageKey} 按失败模式处理。", storageKey);
            return null;
        }

        switch ((verdict.GetString() ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "clean":
                return EvidenceScanStatus.Clean;

            case "rejected":
                var reason = document.RootElement.TryGetProperty("reason", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : "未给出原因";
                logger.LogWarning("内容扫描判定 {StorageKey} 为 rejected：{Reason}", storageKey, reason);
                return EvidenceScanStatus.Rejected;

            case "pending":
                return EvidenceScanStatus.Pending;

            default:
                logger.LogError("内容扫描服务返回了无法识别的判定 {Verdict}；{StorageKey} 按失败模式处理。", verdict.GetString(), storageKey);
                return null;
        }
    }

    /// <summary>扫描不可用时的兜底：closed 保持 Pending（保留文件但不可下载），open 放行。</summary>
    private EvidenceScanStatus Unavailable() =>
        settings.GetChoice(SettingKeys.EvidenceScannerFailMode, "closed") == "open"
            ? EvidenceScanStatus.Clean
            : EvidenceScanStatus.Pending;
}
