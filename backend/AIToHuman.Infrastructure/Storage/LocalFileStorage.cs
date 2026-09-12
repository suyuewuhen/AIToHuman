using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Common;
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
public sealed class SettingsFileStorage(
    ISettingsProvider settings,
    LocalFileStorage local,
    S3FileStorage s3) : IFileStorage, IPresignedFileStorage
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
    /// 只有支持直连下载的存储才算支持；配置写坏（例如 s3 缺参数）时返回 false，
    /// 让列表接口照常工作，真正发起下载时再报出具体缺哪一项。
    /// </summary>
    public bool SupportsPresignedDownload
    {
        get
        {
            try
            {
                return Active() is IPresignedFileStorage { SupportsPresignedDownload: true };
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>按当前 provider 转发预签名请求；本机目录存储不支持，给出可行动的提示。</summary>
    public PresignedDownload CreatePresignedDownload(string storageKey, TimeSpan lifetime, string? downloadFileName = null) =>
        Active() is IPresignedFileStorage presigned
            ? presigned.CreatePresignedDownload(storageKey, lifetime, downloadFileName)
            : throw new DomainException("当前是本机目录存储，不能签发短时直连下载地址：请改用鉴权后的 /content 接口下载，或把 storage.provider 切到 s3。");

    /// <summary>
    /// 当前生效的实现。配置成 s3 时如果关键参数没填全，会由 <see cref="S3FileStorage"/> 直接报错，
    /// 而不是悄悄退回本机目录——静默降级会把“以为存到对象存储”的文件留在容器磁盘上。
    /// </summary>
    private IFileStorage Active() => settings.GetChoice(SettingKeys.StorageProvider, "local") switch
    {
        "local" => local,
        "s3" => s3,
        var other => throw new InvalidOperationException($"未知的对象存储类型 {other}：只支持 local 或 s3，请在运营后台修正 storage.provider。")
    };
}
