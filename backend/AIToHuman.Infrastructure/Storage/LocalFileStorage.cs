using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using Microsoft.Extensions.Logging;

namespace AIToHuman.Infrastructure.Storage;

/// <summary>
/// 本机目录实现，用于开发与试点：写入、读取、删除都限制在根目录内，避免存储键越界访问其他文件。
/// 根目录来自运营配置 <c>storage.localRoot</c>，留空时用应用目录下的 <c>evidence</c>；
/// 每次操作都重新解析根目录，因此运营后台改完目录立即生效，不需要重启进程。
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly ISettingsProvider settings;
    private readonly ILogger<LocalFileStorage> logger;
    private bool loggedRoot;

    public LocalFileStorage(ISettingsProvider settings, ILogger<LocalFileStorage> logger)
    {
        this.settings = settings;
        this.logger = logger;
    }

    /// <summary>当前生效的根目录；暴露出来便于自检与排查“文件到底写哪了”。</summary>
    public string Root
    {
        get
        {
            var configured = settings.GetValue(SettingKeys.StorageLocalRoot);
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(AppContext.BaseDirectory, "evidence")
                : configured);
        }
    }

    public async Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(File.Exists(ResolvePath(key)));

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("存储键不能为空。");

        var root = Root;
        LogRootOnce(root);
        var full = Path.GetFullPath(Path.Combine(root, key));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("存储键越出存储根目录，已拒绝访问。");
        return full;
    }

    private void LogRootOnce(string root)
    {
        if (loggedRoot) return;
        loggedRoot = true;
        logger.LogInformation("凭证文件本机存储根目录：{Root}", root);
    }
}

/// <summary>
/// 按运营配置 <c>storage.provider</c> 选择真正的存储实现。
/// 每次调用都重新判断，因此运营后台在 local 与 s3 之间切换不需要重启进程。
/// </summary>
public sealed class SettingsFileStorage(ISettingsProvider settings, LocalFileStorage local) : IFileStorage
{
    public Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default) =>
        Active().SaveAsync(key, content, contentType, cancellationToken);

    public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default) =>
        Active().OpenReadAsync(key, cancellationToken);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Active().ExistsAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        Active().DeleteAsync(key, cancellationToken);

    /// <summary>
    /// 当前生效的实现。配置成 s3 而代码里还没有对应实现时直接报错，
    /// 而不是悄悄退回本机目录——静默降级会把“以为存到对象存储”的文件留在容器磁盘上。
    /// </summary>
    private IFileStorage Active()
    {
        var provider = settings.GetChoice(SettingKeys.StorageProvider, "local");
        if (provider == "local") return local;

        throw new InvalidOperationException(
            $"对象存储 {provider} 尚未接入：请把 storage.provider 改回 local，或先补上对应的 IFileStorage 实现（S3 兼容实现需要 S3 SDK，本仓库离线环境无法还原该依赖）。");
    }
}
