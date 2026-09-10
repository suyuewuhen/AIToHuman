namespace AIToHuman.Application.Orders;

/// <summary>
/// 私有文件存储。Development 使用本机目录实现；生产应替换为 S3/OSS 等私有 Bucket，
/// 下载授权仍由应用层判定后再签发短时 URL 或流式转发。
/// </summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}
