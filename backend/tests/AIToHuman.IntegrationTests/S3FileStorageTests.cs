using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIToHuman.Application.Settings;
using AIToHuman.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// S3 兼容存储的请求层：签名头、路径、错误映射与配置校验。
/// 这里只验证请求形态与行为；签名本身是否正确由 MinIO 真机联调负责（见 handoff 第 11 节）。
/// </summary>
public sealed class S3FileStorageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Payload = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02];

    [Fact]
    public async Task Save_puts_the_object_with_a_signed_request()
    {
        var recorder = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var storage = Build(recorder);

        await storage.SaveAsync("order/file.png", new MemoryStream(Payload), "image/png");

        var request = Assert.Single(recorder.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("http://127.0.0.1:9000/aitohuman-evidence/evidence/order/file.png", request.Url);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=minioadmin/20260912/us-east-1/s3/aws4_request", request.Authorization);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", request.Authorization);
        Assert.Equal("20260912T080000Z", request.AmzDate);
        Assert.Equal(Sha256Hex(Payload), request.ContentSha256);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal(Payload, request.Body);
    }

    [Fact]
    public async Task Signature_is_deterministic_and_depends_on_the_secret()
    {
        var first = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var second = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var otherSecret = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Build(first).SaveAsync("a.png", new MemoryStream(Payload), "image/png");
        await Build(second).SaveAsync("a.png", new MemoryStream(Payload), "image/png");
        var config = Configuration();
        config[SettingKeys.StorageS3SecretAccessKey] = "another-secret";
        await Build(otherSecret, config).SaveAsync("a.png", new MemoryStream(Payload), "image/png");

        var signature = first.Requests[0].Signature;
        Assert.Equal(signature, second.Requests[0].Signature);
        Assert.Matches("^[0-9a-f]{64}$", signature!);
        Assert.NotEqual(signature, otherSecret.Requests[0].Signature);
    }

    [Fact]
    public async Task Empty_prefix_does_not_produce_a_double_slash()
    {
        var recorder = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var config = Configuration();
        config[SettingKeys.StorageS3Prefix] = "";

        await Build(recorder, config).SaveAsync("a.png", new MemoryStream(Payload), "image/png");

        Assert.Equal("http://127.0.0.1:9000/aitohuman-evidence/a.png", recorder.Requests[0].Url);
    }

    [Fact]
    public async Task Read_returns_content_and_treats_404_as_missing()
    {
        var found = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) });
        var missing = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await using var content = await Build(found).OpenReadAsync("a.png");
        Assert.NotNull(content);
        using var buffer = new MemoryStream();
        await content!.CopyToAsync(buffer);
        Assert.Equal(Payload, buffer.ToArray());
        Assert.Equal("GET", found.Requests[0].Method);

        Assert.Null(await Build(missing).OpenReadAsync("a.png"));
    }

    [Fact]
    public async Task Exists_uses_head_and_reports_404_as_false()
    {
        var present = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var absent = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.True(await Build(present).ExistsAsync("a.png"));
        Assert.Equal("HEAD", present.Requests[0].Method);
        Assert.False(await Build(absent).ExistsAsync("a.png"));
    }

    [Fact]
    public async Task Delete_tolerates_missing_objects()
    {
        var recorder = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var missing = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        await Build(recorder).DeleteAsync("a.png");
        await Build(missing).DeleteAsync("a.png");

        Assert.Equal("DELETE", recorder.Requests[0].Method);
    }

    [Fact]
    public async Task Forbidden_response_becomes_an_actionable_error()
    {
        const string body = "<Error><Code>SignatureDoesNotMatch</Code><Message>The request signature we calculated does not match.</Message></Error>";
        var recorder = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(body, Encoding.UTF8, "application/xml") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build(recorder).SaveAsync("a.png", new MemoryStream(Payload), "image/png"));

        Assert.Contains("SignatureDoesNotMatch", error.Message);
        Assert.Contains("accessKeyId", error.Message);
    }

    [Fact]
    public async Task Missing_configuration_is_reported_key_by_key_without_calling_the_network()
    {
        var recorder = new S3RequestRecorder(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var storage = Build(recorder, new Dictionary<string, string>
        {
            [SettingKeys.StorageS3Endpoint] = "http://127.0.0.1:9000"
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.SaveAsync("a.png", new MemoryStream(Payload), "image/png"));

        Assert.Contains("还没有配置完整", error.Message);
        Assert.Contains(SettingKeys.StorageS3Bucket, error.Message);
        Assert.Contains(SettingKeys.StorageS3SecretAccessKey, error.Message);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Unreachable_endpoint_is_reported_with_the_address()
    {
        var recorder = new S3RequestRecorder(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Build(recorder).SaveAsync("a.png", new MemoryStream(Payload), "image/png"));

        Assert.Contains("无法连接对象存储 http://127.0.0.1:9000", error.Message);
    }

    private static Dictionary<string, string> Configuration() => new()
    {
        [SettingKeys.StorageS3Endpoint] = "http://127.0.0.1:9000",
        [SettingKeys.StorageS3Region] = "us-east-1",
        [SettingKeys.StorageS3Bucket] = "aitohuman-evidence",
        [SettingKeys.StorageS3AccessKeyId] = "minioadmin",
        [SettingKeys.StorageS3SecretAccessKey] = "minioadmin",
        [SettingKeys.StorageS3Prefix] = "evidence"
    };

    private static S3FileStorage Build(S3RequestRecorder recorder, Dictionary<string, string>? configuration = null) =>
        new(
            new StubSettingsProvider(configuration ?? Configuration()),
            new HttpClient(recorder) { Timeout = Timeout.InfiniteTimeSpan },
            new FixedTimeProvider(Now),
            NullLogger<S3FileStorage>.Instance);

    /// <summary>用固定时间戳算出期望值，避免测试里再写一遍日期格式。</summary>
    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>记录 S3 请求形态的替身；签名是否被真实服务接受由 MinIO 联调验证。</summary>
    private sealed class S3RequestRecorder(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<S3Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new S3Request(
                request.Method.Method,
                request.RequestUri?.ToString(),
                Header(request, "Authorization"),
                Header(request, "x-amz-date"),
                Header(request, "x-amz-content-sha256"),
                request.Content?.Headers.ContentType?.MediaType,
                body));

            return responder(request);
        }

        private static string? Header(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

        internal sealed record S3Request(
            string Method,
            string? Url,
            string? Authorization,
            string? AmzDate,
            string? ContentSha256,
            string? ContentType,
            byte[] Body)
        {
            /// <summary>从 Authorization 头里取出签名，用于比较确定性。</summary>
            public string? Signature => Authorization is null
                ? null
                : Authorization.Split("Signature=")[^1];
        }
    }
}
