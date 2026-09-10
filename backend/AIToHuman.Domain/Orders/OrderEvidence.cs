using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Orders;

public enum EvidenceScanStatus
{
    /// <summary>已上传但尚未完成安全检查，此时不可下载。</summary>
    Pending,
    Clean,
    Rejected
}

/// <summary>
/// 订单执行凭证的元数据；文件本身存放在私有对象存储，通过系统生成的存储键访问。
/// 原始文件名只作为展示元数据，永不参与路径拼接。
/// </summary>
public sealed class OrderEvidence
{
    public const int MaxSizeBytes = 5 * 1024 * 1024;
    public const int MaxPerOrder = 10;
    public const int MaxFileNameLength = 200;

    /// <summary>允许的凭证类型白名单；判断依据是服务端解析出的 MIME，不是扩展名。</summary>
    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["application/pdf"] = "pdf"
    };

    private OrderEvidence() { }

    public OrderEvidence(Guid orderId, Guid uploadedBy, string fileName, string contentType, long sizeBytes, string contentHash, DateTimeOffset createdAt)
    {
        if (orderId == Guid.Empty) throw new DomainException("凭证必须关联有效订单。");
        if (uploadedBy == Guid.Empty) throw new DomainException("凭证必须有上传者。");

        var normalizedType = NormalizeContentType(contentType);
        EnsureSupportedContentType(normalizedType);
        var extension = AllowedContentTypes[normalizedType];
        if (sizeBytes is < 1 or > MaxSizeBytes)
            throw new DomainException($"凭证大小必须在 1 字节到 {MaxSizeBytes / 1024 / 1024} MB 之间。");

        var trimmedName = (fileName ?? string.Empty).Trim();
        if (trimmedName.Length > MaxFileNameLength) throw new DomainException($"文件名不能超过 {MaxFileNameLength} 个字符。");
        if (string.IsNullOrWhiteSpace(contentHash)) throw new DomainException("凭证必须包含内容摘要。");

        Id = Guid.NewGuid();
        OrderId = orderId;
        UploadedBy = uploadedBy;
        FileName = string.IsNullOrEmpty(trimmedName) ? $"evidence.{extension}" : trimmedName;
        ContentType = normalizedType;
        // 存储键完全由系统生成，杜绝客户端传入路径片段。
        StorageKey = $"{orderId:N}/{Id:N}.{extension}";
        SizeBytes = sizeBytes;
        ContentHash = contentHash;
        CreatedAt = UtcTimestamp.Normalize(createdAt);
        ScanStatus = EvidenceScanStatus.Pending;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid UploadedBy { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public string StorageKey { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public EvidenceScanStatus ScanStatus { get; private set; }
    public DateTimeOffset? ScannedAt { get; private set; }

    /// <summary>只有通过安全检查的凭证才能下载。</summary>
    public bool IsDownloadable => ScanStatus == EvidenceScanStatus.Clean;

    public static OrderEvidence Rehydrate(
        Guid id,
        Guid orderId,
        Guid uploadedBy,
        string fileName,
        string contentType,
        string storageKey,
        long sizeBytes,
        string contentHash,
        DateTimeOffset createdAt,
        EvidenceScanStatus scanStatus,
        DateTimeOffset? scannedAt) => new(orderId, uploadedBy, fileName, contentType, sizeBytes, contentHash, createdAt)
        {
            Id = id,
            StorageKey = storageKey,
            ScanStatus = scanStatus,
            ScannedAt = scannedAt is null ? null : UtcTimestamp.Normalize(scannedAt.Value)
        };

    /// <summary>写入扫描结果。Pending 之外的状态是终态，不允许回退或改写。</summary>
    public void MarkScanned(EvidenceScanStatus status, DateTimeOffset now)
    {
        if (status == EvidenceScanStatus.Pending) throw new DomainException("扫描结果不能是待处理。");
        if (ScanStatus != EvidenceScanStatus.Pending) throw new DomainException("凭证的扫描结果已经确定，不能重复写入。");
        ScanStatus = status;
        ScannedAt = UtcTimestamp.Normalize(now);
    }

    /// <summary>去掉 charset 等参数并转小写，例如 <c>image/png; charset=binary</c>。</summary>
    public static string NormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;
        var separator = contentType.IndexOf(';');
        var mediaType = separator >= 0 ? contentType[..separator] : contentType;
        return mediaType.Trim().ToLowerInvariant();
    }

    public static bool IsSupportedContentType(string? contentType) => AllowedContentTypes.ContainsKey(NormalizeContentType(contentType));

    /// <summary>白名单校验；应用层在读取内容之前先用它拦截，避免为非法类型做无谓的 IO。</summary>
    public static void EnsureSupportedContentType(string? contentType)
    {
        if (!IsSupportedContentType(contentType))
            throw new DomainException($"凭证类型不受支持：{contentType}。允许的类型为图片（JPEG/PNG/WebP）和 PDF。");
    }

    /// <summary>
    /// 校验文件签名与声明的 MIME 是否一致：服务端不信任扩展名，也不信任客户端声明的类型。
    /// </summary>
    public static void EnsureContentMatchesType(string contentType, ReadOnlySpan<byte> content)
    {
        var normalized = NormalizeContentType(contentType);
        var matches = normalized switch
        {
            "image/png" => content.Length >= 8 && content[..8].SequenceEqual(PngSignature),
            "image/jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            "image/webp" => content.Length >= 12
                && content[..4].SequenceEqual("RIFF"u8)
                && content.Slice(8, 4).SequenceEqual("WEBP"u8),
            "application/pdf" => content.Length >= 4 && content[..4].SequenceEqual("%PDF"u8),
            _ => false
        };

        if (!matches) throw new DomainException("凭证内容与声明的类型不一致，已拒绝保存。");
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string ExtensionFor(string contentType) =>
        AllowedContentTypes.TryGetValue(NormalizeContentType(contentType), out var extension) ? extension : "bin";
}
