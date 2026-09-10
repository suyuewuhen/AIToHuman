using AIToHuman.Application.Orders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AIToHuman.Infrastructure.Storage;

public sealed class ObjectStorageOptions
{
    /// <summary>本机存储根目录；为空时使用应用目录下的 <c>evidence</c>。生产应改用私有对象存储。</summary>
    public string LocalRoot { get; set; } = string.Empty;
}

/// <summary>
/// 本机目录实现，用于开发与测试：写入、读取、删除都限制在根目录内，避免存储键越界访问其他文件。
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string root;

    public LocalFileStorage(IOptions<ObjectStorageOptions> options, ILogger<LocalFileStorage> logger)
    {
        var configured = options.Value.LocalRoot;
        root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "evidence") : configured);
        Directory.CreateDirectory(root);
        logger.LogInformation("凭证文件存储根目录：{Root}", root);
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
        var full = Path.GetFullPath(Path.Combine(root, key));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("存储键越出存储根目录，已拒绝访问。");
        return full;
    }
}

/// <summary>
/// 占位扫描实现：没有接入真实病毒/内容检查，一律放行并打警告日志，
/// 避免让调用方误以为文件已经被检查过。生产环境必须替换。
/// </summary>
public sealed class NoOpEvidenceScanner(ILogger<NoOpEvidenceScanner> logger) : IEvidenceScanner
{
    public Task<Domain.Orders.EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("尚未接入凭证安全检查，已直接放行 {StorageKey}（类型 {ContentType}）。生产环境必须替换 IEvidenceScanner 实现。", storageKey, contentType);
        return Task.FromResult(Domain.Orders.EvidenceScanStatus.Clean);
    }
}
