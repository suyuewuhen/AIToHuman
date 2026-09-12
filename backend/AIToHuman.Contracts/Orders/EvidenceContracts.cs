namespace AIToHuman.Contracts.Orders;

public sealed record EvidenceResponse(
    Guid Id,
    Guid OrderId,
    Guid UploadedBy,
    string FileName,
    string ContentType,
    long SizeBytes,
    string ContentHash,
    string ScanStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ScannedAt,
    bool IsDownloadable,
    int ScanAttempts,
    string? LastScanNote,
    bool ScanExhausted,
    // 当前存储是否支持短时直连下载地址；为 false 时客户端走鉴权后的 /content 下载。
    bool PresignedDownloadAvailable,
    // 上传时被剥离的元数据（例如 EXIF/XMP、PNG 文本块）；为空表示没剥或不需要剥。
    string? MetadataRemoved);

/// <summary>短时直连下载地址；有效期由运营配置决定，过期后需要重新申请。</summary>
public sealed record EvidenceDownloadUrlResponse(string Url, DateTimeOffset ExpiresAt);
