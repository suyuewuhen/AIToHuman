namespace AIToHuman.Application.Orders;

/// <summary>
/// 私有文件存储。本机目录与 S3 兼容对象存储两种实现由运营配置选择；
/// 读取一律要先在应用层判定权限，再决定是流式转发还是签发短时直连地址。
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// 可选能力：能签发“短时直连下载地址”的存储实现。
/// 对象存储（S3 兼容）支持，本机目录存储不支持——调用方要先看 <see cref="SupportsPresignedDownload"/>，
/// 不能假设所有实现都有这个能力。
/// </summary>
public interface IPresignedFileStorage
{
    /// <summary>当前生效的存储是否支持直连下载（切到本机目录时为 false）。</summary>
    bool SupportsPresignedDownload { get; }

    /// <summary>
    /// 为某个存储键签发短时下载地址。<paramref name="downloadFileName"/> 不为空时，
    /// 地址会带上让浏览器直接另存为附件的响应头参数（这个参数同样参与签名）。
    /// </summary>
    PresignedDownload CreatePresignedDownload(string storageKey, TimeSpan lifetime, string? downloadFileName = null);
}

/// <summary>短时直连下载地址与它的失效时间。</summary>
public sealed record PresignedDownload(string Url, DateTimeOffset ExpiresAt);
