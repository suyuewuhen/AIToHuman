using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using Microsoft.Extensions.Logging;

namespace AIToHuman.Infrastructure.Storage;

/// <summary>
/// S3 兼容对象存储（MinIO、阿里云 OSS、AWS S3 等）的实现。
/// 刻意不依赖厂商 SDK：只用 <see cref="HttpClient"/> 加自己实现的 AWS Signature V4，
/// 这样离线环境也能编译，换服务商只需要改运营配置里的 endpoint / region / bucket / 密钥。
///
/// 约定：
/// <list type="bullet">
/// <item>路径风格（path-style）请求：<c>{endpoint}/{bucket}/{prefix}{key}</c>，MinIO 与各类兼容服务都支持。</item>
/// <item>只签 <c>host</c>、<c>x-amz-content-sha256</c>、<c>x-amz-date</c> 三个头，载荷用完整的 SHA-256（凭证有大小上限，可以整体摘要）。</item>
/// <item>401/403 翻译成“密钥或权限不对”，404 在读取与存在性判断里是正常结果。</item>
/// </list>
/// </summary>
public sealed class S3FileStorage(
    ISettingsProvider settings,
    HttpClient httpClient,
    TimeProvider timeProvider,
    ILogger<S3FileStorage> logger) : IFileStorage
{
    /// <summary>S3 的空载荷摘要（SHA-256 of empty string）。</summary>
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string Algorithm = "AWS4-HMAC-SHA256";

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllAsync(content, cancellationToken);
        var payloadHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var config = Configure();

        using var request = CreateRequest(config, HttpMethod.Put, key, payloadHash);
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await SendAsync(config, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync("写入对象", key, response, cancellationToken);
    }

    public async Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var config = Configure();
        using var request = CreateRequest(config, HttpMethod.Get, key, EmptyPayloadHash);
        using var response = await SendAsync(config, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync("读取对象", key, response, cancellationToken);

        // 先读进内存再返回：调用方拿到的流必须比 HttpClient 的响应活得久。
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new MemoryStream(bytes, writable: false);
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        var config = Configure();
        using var request = CreateRequest(config, HttpMethod.Head, key, EmptyPayloadHash);
        using var response = await SendAsync(config, request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync("检查对象", key, response, cancellationToken);

        return true;
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var config = Configure();
        using var request = CreateRequest(config, HttpMethod.Delete, key, EmptyPayloadHash);
        using var response = await SendAsync(config, request, cancellationToken);
        // 对象本来就不在也算成功：调用方关心的是“删完之后它不在”。
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync("删除对象", key, response, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(S3Configuration config, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await config.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException($"无法连接对象存储 {config.Endpoint}：{exception.Message}", exception);
        }
    }

    private HttpRequestMessage CreateRequest(S3Configuration config, HttpMethod method, string key, string payloadHash)
    {
        // 自己拼规范路径（而不是从 Uri.AbsolutePath 反推），避免 Uri 规范化悄悄改掉转义结果导致签名不符。
        var canonicalPath = string.Concat("/", config.Bucket, "/", config.Prefix, EscapeKey(key));
        var request = new HttpRequestMessage(method, config.Endpoint + canonicalPath);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var now = timeProvider.GetUtcNow();
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

        request.Headers.TryAddWithoutValidation("Authorization", Sign(config, method, canonicalPath, payloadHash, amzDate, dateStamp));
        return request;
    }

    /// <summary>AWS Signature V4：规范请求 → 待签字符串 → 逐级派生签名密钥 → Authorization 头。</summary>
    private static string Sign(S3Configuration config, HttpMethod method, string canonicalPath, string payloadHash, string amzDate, string dateStamp)
    {
        var host = config.Host;
        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";

        var canonicalRequest = string.Join('\n',
            method.Method,
            canonicalPath,
            string.Empty,
            canonicalHeaders,
            signedHeaders,
            payloadHash);

        var scope = $"{dateStamp}/{config.Region}/s3/aws4_request";
        var stringToSign = string.Join('\n',
            Algorithm,
            amzDate,
            scope,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))).ToLowerInvariant());

        var signingKey = DeriveSigningKey(config.SecretAccessKey, dateStamp, config.Region);
        var signature = Convert.ToHexString(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();

        return $"{Algorithm} Credential={config.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] DeriveSigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var dateKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), Encoding.UTF8.GetBytes(dateStamp));
        var regionKey = HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regionKey, Encoding.UTF8.GetBytes("s3"));
        return HMACSHA256.HashData(serviceKey, Encoding.UTF8.GetBytes("aws4_request"));
    }

    /// <summary>逐段转义对象键，保留 <c>/</c> 作为层级分隔符。</summary>
    private static string EscapeKey(string key) =>
        string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>把 S3 的错误响应翻译成能直接行动的说明，而不是只丢一个状态码。</summary>
    private async Task<InvalidOperationException> CreateFailureAsync(string action, string key, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var code = ExtractXmlValue(detail, "Code");
        var message = ExtractXmlValue(detail, "Message");
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                "请检查 storage.s3.accessKeyId / secretAccessKey / region 是否正确，以及该密钥是否有这个 Bucket 的读写权限。",
            HttpStatusCode.NotFound =>
                "Bucket 不存在：请先在对象存储里创建 storage.s3.bucket 指向的私有 Bucket。",
            _ => "请检查 endpoint、bucket、region 与网络连通性。"
        };

        logger.LogError(
            "对象存储操作失败：{Action} {Key} 返回 {Status}（{Code}）。{Hint}",
            action,
            key,
            (int)response.StatusCode,
            code ?? "unknown",
            hint);

        var text = string.IsNullOrWhiteSpace(code)
            ? $"{(int)response.StatusCode} {response.ReasonPhrase}"
            : $"{(int)response.StatusCode} {code}：{message}";

        return new InvalidOperationException($"对象存储{action}失败（{text}）。{hint}");
    }

    /// <summary>S3 的错误体是 XML；这里只取需要的两个字段，不为它引入 XML 依赖。</summary>
    private static string? ExtractXmlValue(string xml, string element)
    {
        var open = xml.IndexOf($"<{element}>", StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;
        var close = xml.IndexOf($"</{element}>", open, StringComparison.OrdinalIgnoreCase);
        if (close < 0) return null;
        return xml[(open + element.Length + 2)..close].Trim();
    }

    /// <summary>读取运营配置并做完整性校验：缺哪一项就报哪一项，避免出现“签名算错”这种难查的现象。</summary>
    private S3Configuration Configure()
    {
        var endpoint = settings.GetValue(SettingKeys.StorageS3Endpoint)?.TrimEnd('/');
        var region = settings.GetValue(SettingKeys.StorageS3Region);
        var bucket = settings.GetValue(SettingKeys.StorageS3Bucket);
        var accessKeyId = settings.GetValue(SettingKeys.StorageS3AccessKeyId);
        var secretAccessKey = settings.GetValue(SettingKeys.StorageS3SecretAccessKey);
        var prefix = settings.GetValue(SettingKeys.StorageS3Prefix) ?? string.Empty;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(endpoint)) missing.Add(SettingKeys.StorageS3Endpoint);
        if (string.IsNullOrWhiteSpace(region)) missing.Add(SettingKeys.StorageS3Region);
        if (string.IsNullOrWhiteSpace(bucket)) missing.Add(SettingKeys.StorageS3Bucket);
        if (string.IsNullOrWhiteSpace(accessKeyId)) missing.Add(SettingKeys.StorageS3AccessKeyId);
        if (string.IsNullOrWhiteSpace(secretAccessKey)) missing.Add(SettingKeys.StorageS3SecretAccessKey);
        if (missing.Count > 0)
            throw new InvalidOperationException($"对象存储还没有配置完整：请在运营后台补齐 {string.Join("、", missing)}。");

        var host = new Uri(endpoint!).Authority;
        var trimmedPrefix = prefix.Trim('/');
        return new S3Configuration(
            endpoint!,
            host,
            region!,
            bucket!,
            accessKeyId!,
            secretAccessKey!,
            trimmedPrefix.Length == 0 ? string.Empty : trimmedPrefix + "/",
            httpClient);
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        // 常见情况是 MemoryStream，能直接拿到缓冲区就不用再复制一次。
        if (content is MemoryStream memory && memory.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length)
            return segment.Array;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private sealed record S3Configuration(
        string Endpoint,
        string Host,
        string Region,
        string Bucket,
        string AccessKeyId,
        string SecretAccessKey,
        string Prefix,
        HttpClient Client);
}
