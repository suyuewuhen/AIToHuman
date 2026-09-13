using System.Net;
using System.Net.Http.Headers;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// S3 兼容对象存储的真实回归：往返读写、预签名直连地址可用、以及**篡改签名/换对象路径/过期**
/// 都必须被对象存储自己拒绝。最后三条是真正有价值的部分——它们验证的是我们自研的 SigV4
/// 不是"看起来像签名"，而是真的被服务端认账。
/// </summary>
public sealed class S3StorageTests
{
    [MinioFact]
    public async Task Round_trips_an_object_and_reports_existence()
    {
        var options = ExternalTestEnvironment.S3!;
        using var client = NewClient();
        var storage = ExternalTestEnvironment.CreateStorage(options, client);
        var key = Key("round-trip");
        var payload = "凭证正文：中文与 emoji 🙂"u8.ToArray();

        await storage.SaveAsync(key, new MemoryStream(payload), "text/plain");
        try
        {
            Assert.True(await storage.ExistsAsync(key));

            await using var read = await storage.OpenReadAsync(key);
            Assert.NotNull(read);
            using var buffer = new MemoryStream();
            await read!.CopyToAsync(buffer);
            Assert.Equal(payload, buffer.ToArray());
        }
        finally
        {
            await storage.DeleteAsync(key);
        }

        Assert.False(await storage.ExistsAsync(key));
        // 不存在的对象读取返回 null，而不是抛异常（404 在读取语义里是正常结果）。
        Assert.Null(await storage.OpenReadAsync(key));
    }

    [MinioFact]
    public async Task A_presigned_url_serves_the_bytes_without_any_credentials()
    {
        var options = ExternalTestEnvironment.S3!;
        using var client = NewClient();
        var storage = ExternalTestEnvironment.CreateStorage(options, client);
        var key = Key("presigned");
        var payload = "预签名直连下载"u8.ToArray();

        await storage.SaveAsync(key, new MemoryStream(payload), "text/plain");
        try
        {
            var presigned = storage.CreatePresignedDownload(key, TimeSpan.FromMinutes(2), "evidence-demo.txt");
            Assert.True(presigned.ExpiresAt > DateTimeOffset.UtcNow);

            // 关键：不带任何 Authorization 头，就像一个普通浏览器那样去取。
            using var response = await client.GetAsync(presigned.Url);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(payload, await response.Content.ReadAsByteArrayAsync());
            // 附件名参与签名，所以服务端会原样回给我们。
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Contains("evidence-demo.txt", response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);
        }
        finally
        {
            await storage.DeleteAsync(key);
        }
    }

    [MinioFact]
    public async Task A_tampered_signature_is_rejected_by_the_server()
    {
        var options = ExternalTestEnvironment.S3!;
        using var client = NewClient();
        var storage = ExternalTestEnvironment.CreateStorage(options, client);
        var key = Key("tampered");
        var payload = "篡改签名测试"u8.ToArray();

        await storage.SaveAsync(key, new MemoryStream(payload), "text/plain");
        try
        {
            var url = storage.CreatePresignedDownload(key, TimeSpan.FromMinutes(2)).Url;
            // 把签名最后一个字符改掉：服务端必须拒绝，而不是照发。
            var tampered = url[..^1] + (url[^1] == 'A' ? 'B' : 'A');

            using var response = await client.GetAsync(tampered);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await storage.DeleteAsync(key);
        }
    }

    [MinioFact]
    public async Task Swapping_the_object_path_is_rejected_by_the_server()
    {
        var options = ExternalTestEnvironment.S3!;
        using var client = NewClient();
        var storage = ExternalTestEnvironment.CreateStorage(options, client);
        var mine = Key("path-mine");
        var other = Key("path-other");

        await storage.SaveAsync(mine, new MemoryStream("mine"u8.ToArray()), "text/plain");
        await storage.SaveAsync(other, new MemoryStream("other"u8.ToArray()), "text/plain");
        try
        {
            var presigned = storage.CreatePresignedDownload(mine, TimeSpan.FromMinutes(2)).Url;
            // 路径也参与签名：拿 A 的签名去取 B 必须失败，否则短时地址就成了万能钥匙。
            var swapped = presigned.Replace(mine, other, StringComparison.Ordinal);

            // 先确认替换真的改了地址（URL 里的键是未转义的路径片段），否则这条用例会"因为没改而通过"。
            Assert.NotEqual(presigned, swapped);

            using var response = await client.GetAsync(swapped);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await storage.DeleteAsync(mine);
            await storage.DeleteAsync(other);
        }
    }

    [MinioFact]
    public async Task An_expired_presigned_url_is_rejected_by_the_server()
    {
        var options = ExternalTestEnvironment.S3!;
        using var client = NewClient();
        var storage = ExternalTestEnvironment.CreateStorage(options, client);
        var key = Key("expired");

        await storage.SaveAsync(key, new MemoryStream("过期测试"u8.ToArray()), "text/plain");
        try
        {
            // 有效期是**服务端**按 X-Amz-Date + X-Amz-Expires 与它自己的时钟判定的，
            // 所以这里只能真的等过去（改客户端时钟没用，那样测的是我们自己的字段而不是服务端的判定）。
            var presigned = storage.CreatePresignedDownload(key, TimeSpan.FromSeconds(5));

            using (var fresh = await client.GetAsync(presigned.Url))
            {
                Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            }

            await Task.Delay(TimeSpan.FromSeconds(7));

            using var expired = await client.GetAsync(presigned.Url);

            Assert.Equal(HttpStatusCode.Forbidden, expired.StatusCode);
        }
        finally
        {
            await storage.DeleteAsync(key);
        }
    }

    private static HttpClient NewClient() => new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>每个用例用自己的键前缀，避免并行或残留互相干扰。</summary>
    private static string Key(string name) => $"tests/{name}-{Guid.NewGuid():N}.txt";
}
