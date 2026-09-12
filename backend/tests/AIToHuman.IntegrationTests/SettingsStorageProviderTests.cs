using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Settings;
using AIToHuman.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

public sealed class SettingsStorageProviderTests
{
    [Fact]
    public async Task Local_root_comes_from_settings_and_round_trips()
    {
        var root = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?> { ["ObjectStorage:LocalRoot"] = root });
            var storage = new LocalFileStorage(host.Provider, NullLogger<LocalFileStorage>.Instance);

            await storage.SaveAsync("orders/1.png", new MemoryStream(new byte[] { 1, 2, 3 }), "image/png");

            Assert.True(await storage.ExistsAsync("orders/1.png"));
            Assert.True(File.Exists(Path.Combine(root, "orders", "1.png")));

            byte[] read;
            await using (var content = await storage.OpenReadAsync("orders/1.png"))
            {
                Assert.NotNull(content);
                using var buffer = new MemoryStream();
                await content!.CopyToAsync(buffer);
                read = buffer.ToArray();
            }

            Assert.Equal(new byte[] { 1, 2, 3 }, read);

            await storage.DeleteAsync("orders/1.png");
            Assert.False(await storage.ExistsAsync("orders/1.png"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Changing_the_root_in_the_console_takes_effect_without_restarting()
    {
        var first = CreateScratchDirectory();
        var second = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?> { ["ObjectStorage:LocalRoot"] = first });
            var storage = new LocalFileStorage(host.Provider, NullLogger<LocalFileStorage>.Instance);
            await storage.SaveAsync("first.bin", new MemoryStream(new byte[] { 1 }), "application/octet-stream");
            Assert.True(File.Exists(Path.Combine(first, "first.bin")));

            // 运营后台改目录：同一个存储实例下一次写入就用新目录，不需要重启进程。
            await host.Service.UpdateAsync(SettingKeys.StorageLocalRoot, new UpdateSettingRequest(second), SettingsTestData.Admin);
            await storage.SaveAsync("second.bin", new MemoryStream(new byte[] { 2 }), "application/octet-stream");

            Assert.True(File.Exists(Path.Combine(second, "second.bin")));
            Assert.False(File.Exists(Path.Combine(first, "second.bin")));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Theory]
    [InlineData("../escaped.bin")]
    [InlineData("orders/../../escaped.bin")]
    public async Task Keys_cannot_escape_the_storage_root(string key)
    {
        var root = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?> { ["ObjectStorage:LocalRoot"] = root });
            var storage = new LocalFileStorage(host.Provider, NullLogger<LocalFileStorage>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.SaveAsync(key, new MemoryStream(new byte[] { 1 }), "application/octet-stream"));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "escaped.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_local_delegates_to_the_local_implementation()
    {
        var root = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?> { ["ObjectStorage:LocalRoot"] = root });
            var storage = Build(host.Provider, root);

            await storage.SaveAsync("evidence.bin", new MemoryStream(new byte[] { 9 }), "application/octet-stream");

            Assert.True(File.Exists(Path.Combine(root, "evidence.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_s3_sends_the_object_to_the_bucket_instead_of_writing_locally()
    {
        var root = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?>
            {
                ["Settings:storage:provider"] = "s3",
                ["ObjectStorage:LocalRoot"] = root,
                ["Settings:storage:s3:endpoint"] = "http://127.0.0.1:9000",
                ["Settings:storage:s3:region"] = "us-east-1",
                ["Settings:storage:s3:bucket"] = "aitohuman-evidence",
                ["Settings:storage:s3:accessKeyId"] = "minioadmin",
                ["Settings:storage:s3:secretAccessKey"] = "minioadmin",
                ["Settings:storage:s3:prefix"] = "evidence"
            });
            var recorder = new RecordingHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            var storage = Build(host.Provider, root, recorder);

            await storage.SaveAsync("order/a.png", new MemoryStream(new byte[] { 9 }), "image/png");

            var request = Assert.Single(recorder.Requests);
            Assert.Equal("http://127.0.0.1:9000/aitohuman-evidence/evidence/order/a.png", request.Url);
            // 关键点：切到对象存储之后，本机目录里不该再落下任何文件。
            Assert.False(File.Exists(Path.Combine(root, "order", "a.png")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_s3_without_configuration_reports_missing_keys_and_writes_nothing_locally()
    {
        var root = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?>
            {
                ["Settings:storage:provider"] = "s3",
                ["ObjectStorage:LocalRoot"] = root
            });
            var storage = Build(host.Provider, root);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.SaveAsync("evidence.bin", new MemoryStream(new byte[] { 9 }), "application/octet-stream"));

            Assert.Contains("还没有配置完整", exception.Message, StringComparison.Ordinal);
            // 没有静默退回本机目录，否则会以为文件已经进了对象存储。
            Assert.False(File.Exists(Path.Combine(root, "evidence.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>按 provider 组装真实的 SettingsFileStorage；S3 侧用替身 HTTP，避免测试联网。</summary>
    private static SettingsFileStorage Build(AIToHuman.Application.Settings.ISettingsProvider provider, string root, RecordingHttpHandler? handler = null) =>
        new(
            provider,
            new LocalFileStorage(provider, NullLogger<LocalFileStorage>.Instance),
            new S3FileStorage(
                provider,
                new HttpClient(handler ?? new RecordingHttpHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK))) { Timeout = Timeout.InfiniteTimeSpan },
                new FixedTimeProvider(new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero)),
                NullLogger<S3FileStorage>.Instance));

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
