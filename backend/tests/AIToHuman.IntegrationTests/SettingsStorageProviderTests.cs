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
            var storage = new SettingsFileStorage(host.Provider, new LocalFileStorage(host.Provider, NullLogger<LocalFileStorage>.Instance));

            await storage.SaveAsync("evidence.bin", new MemoryStream(new byte[] { 9 }), "application/octet-stream");

            Assert.True(File.Exists(Path.Combine(root, "evidence.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_s3_fails_loudly_instead_of_writing_locally()
    {
        var root = CreateScratchDirectory();
        try
        {
            var host = new TestSettingsHost(new Dictionary<string, string?>
            {
                ["Settings:storage:provider"] = "s3",
                ["ObjectStorage:LocalRoot"] = root
            });
            var local = new LocalFileStorage(host.Provider, NullLogger<LocalFileStorage>.Instance);
            var storage = new SettingsFileStorage(host.Provider, local);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.SaveAsync("evidence.bin", new MemoryStream(new byte[] { 9 }), "application/octet-stream"));

            Assert.Contains("尚未接入", exception.Message, StringComparison.Ordinal);
            // 关键点：没有静默退回本机目录，否则会以为文件已经进了对象存储。
            Assert.False(File.Exists(Path.Combine(root, "evidence.bin")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateScratchDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "settings-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
