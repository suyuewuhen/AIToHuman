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
/// 一次上传要遵守的限额。默认值与硬上限分开：
/// 运营后台可以在硬上限之内收紧（例如把单份上限从 5 MB 调到 2 MB），但不能突破。
/// </summary>
public sealed record EvidenceLimits(long MaxSizeBytes, int MaxPerOrder)
{
    /// <summary>可配置的最小单份上限，避免被调成几百字节导致凭证根本传不上去。</summary>
    public const long MinSizeBytes = 1024;

    /// <summary>代码默认值：与 <see cref="OrderEvidence"/> 的默认常量一致。</summary>
    public static readonly EvidenceLimits Default = new(OrderEvidence.MaxSizeBytes, OrderEvidence.MaxPerOrder);

    /// <summary>硬上限本身，仅供还原历史数据时使用（历史凭证可能大于当前配置的上限）。</summary>
    public static readonly EvidenceLimits Absolute = new(OrderEvidence.AbsoluteMaxSizeBytes, OrderEvidence.AbsoluteMaxPerOrder);

    /// <summary>校验运营提交的限额；越界抛 <see cref="DomainException"/>。</summary>
    public static EvidenceLimits Create(long maxSizeBytes, int maxPerOrder)
    {
        if (maxSizeBytes is < MinSizeBytes or > OrderEvidence.AbsoluteMaxSizeBytes)
            throw new DomainException($"单份凭证上限必须在 {MinSizeBytes / 1024} KB 到 {OrderEvidence.AbsoluteMaxSizeBytes / 1024 / 1024} MB 之间。");
        if (maxPerOrder is < 1 or > OrderEvidence.AbsoluteMaxPerOrder)
            throw new DomainException($"每个订单的凭证份数上限必须在 1 到 {OrderEvidence.AbsoluteMaxPerOrder} 之间。");

        return new EvidenceLimits(maxSizeBytes, maxPerOrder);
    }

    /// <summary>给人看的上限文案：整 MB 时写 MB，否则写 KB 并向上取整，避免 2.5 MB 被显示成 2 MB。</summary>
    public string MaxSizeDisplay => MaxSizeBytes % (1024 * 1024) == 0
        ? $"{MaxSizeBytes / 1024 / 1024} MB"
        : $"{Math.Ceiling(MaxSizeBytes / 1024d):0} KB";

    public void EnsureSizeWithin(long sizeBytes)
    {
        if (sizeBytes < 1 || sizeBytes > MaxSizeBytes)
            throw new DomainException($"凭证大小必须在 1 字节到 {MaxSizeDisplay} 之间。");
    }

    public void EnsureCountWithin(int existingCount)
    {
        if (existingCount >= MaxPerOrder)
            throw new DomainException($"每个订单最多上传 {MaxPerOrder} 份凭证。");
    }
}

/// <summary>
/// 按上传者统计的提交频率上限。用数据库计数而不是内存计数器，
/// 因此多实例部署时每个实例看到的是同一个数字。
/// </summary>
public static class EvidenceUploadQuota
{
    public const int DefaultPerUserPerHour = 60;
    public const int MinPerUserPerHour = 1;
    public const int MaxPerUserPerHour = 1000;

    /// <summary>统计窗口：一小时。</summary>
    public static TimeSpan Window => TimeSpan.FromHours(1);

    public static void EnsureWithin(int recentUploads, int quota)
    {
        if (recentUploads >= quota)
            throw new DomainException($"一小时内的凭证上传次数已达上限（{quota} 次），请稍后再试。");
    }
}

/// <summary>
/// 订单执行凭证的元数据；文件本身存放在私有对象存储，通过系统生成的存储键访问。
/// 原始文件名只作为展示元数据，永不参与路径拼接。
/// 允许的类型白名单刻意留在代码里而不是做成运营配置：放开它等于允许上传可执行内容。
/// </summary>
public sealed class OrderEvidence
{
    /// <summary>单份凭证的默认上限（运营可以在硬上限内调整）。</summary>
    public const int MaxSizeBytes = 5 * 1024 * 1024;

    /// <summary>每个订单的默认份数上限（运营可以在硬上限内调整）。</summary>
    public const int MaxPerOrder = 10;

    /// <summary>单份凭证的硬上限：无论运营怎么配都不会超过。</summary>
    public const long AbsoluteMaxSizeBytes = 25L * 1024 * 1024;

    /// <summary>每个订单凭证份数的硬上限。</summary>
    public const int AbsoluteMaxPerOrder = 50;

    /// <summary>自动重新扫描的最大尝试次数；达到后保留“待扫描”并停止重试，留给人工处理。</summary>
    public const int MaxScanAttempts = 5;

    public const int MaxFileNameLength = 200;
    public const int MaxScanNoteLength = 200;

    /// <summary>“已移除元数据”说明的长度上限。</summary>
    public const int MaxMetadataNoteLength = 200;

    /// <summary>允许的凭证类型白名单；判断依据是服务端解析出的 MIME，不是扩展名。</summary>
    private static readonly Dictionary<string, string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["application/pdf"] = "pdf"
    };

    private OrderEvidence() { }

    /// <summary>
    /// 新建凭证。<paramref name="limits"/> 为当前生效的上传限额（省略即用默认值）；
    /// 还原历史数据时由 <see cref="Rehydrate"/> 传硬上限，避免旧凭证因运营收紧配置而读不出来。
    /// </summary>
    public OrderEvidence(
        Guid orderId,
        Guid uploadedBy,
        string fileName,
        string contentType,
        long sizeBytes,
        string contentHash,
        DateTimeOffset createdAt,
        EvidenceLimits? limits = null)
    {
        if (orderId == Guid.Empty) throw new DomainException("凭证必须关联有效订单。");
        if (uploadedBy == Guid.Empty) throw new DomainException("凭证必须有上传者。");

        var normalizedType = NormalizeContentType(contentType);
        EnsureSupportedContentType(normalizedType);
        var extension = AllowedContentTypes[normalizedType];
        (limits ?? EvidenceLimits.Default).EnsureSizeWithin(sizeBytes);

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

    /// <summary>已经尝试过几次扫描（含自动重试）；用于退避与“别再重试了”的判断。</summary>
    public int ScanAttempts { get; private set; }

    /// <summary>最近一次扫描的说明：通过、拒绝原因，或扫描服务不可用的提示。</summary>
    public string? LastScanNote { get; private set; }

    public DateTimeOffset? LastScanAttemptAt { get; private set; }

    /// <summary>上传时被剥离掉的元数据（例如 EXIF/GPS、PNG 文本块）；为空表示没剥或不需要剥。</summary>
    public string? MetadataRemoved { get; private set; }

    /// <summary>
    /// 记录上传时剥离了哪些元数据。隐私相关的处理结果要能被查到，
    /// 因此这里落库而不是只写日志。
    /// </summary>
    public void RecordStrippedMetadata(IReadOnlyList<string> removed)
    {
        if (removed.Count == 0) return;

        var text = string.Join("、", removed);
        MetadataRemoved = text.Length <= MaxMetadataNoteLength ? text : text[..MaxMetadataNoteLength];
    }

    /// <summary>只有通过安全检查的凭证才能下载。</summary>
    public bool IsDownloadable => ScanStatus == EvidenceScanStatus.Clean;

    /// <summary>还在等待扫描，且没有用完重试次数。</summary>
    public bool CanRetryScan => ScanStatus == EvidenceScanStatus.Pending && ScanAttempts < MaxScanAttempts;

    /// <summary>一直是待扫描但重试次数已用尽：不会再自动重试，需要人工处理。</summary>
    public bool ScanExhausted => ScanStatus == EvidenceScanStatus.Pending && ScanAttempts >= MaxScanAttempts;

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
        DateTimeOffset? scannedAt,
        int scanAttempts = 0,
        string? lastScanNote = null,
        DateTimeOffset? lastScanAttemptAt = null,
        string? metadataRemoved = null)
    {
        // 还原历史数据时只套用硬上限：当前的运营配置可能比上传时更紧，不能因此让旧凭证读不出来。
        var evidence = new OrderEvidence(orderId, uploadedBy, fileName, contentType, sizeBytes, contentHash, createdAt, EvidenceLimits.Absolute)
        {
            Id = id,
            StorageKey = storageKey,
            ScanStatus = scanStatus,
            ScannedAt = scannedAt is null ? null : UtcTimestamp.Normalize(scannedAt.Value),
            ScanAttempts = scanAttempts < 0 ? 0 : scanAttempts,
            LastScanNote = NormalizeNote(lastScanNote),
            LastScanAttemptAt = lastScanAttemptAt is null ? null : UtcTimestamp.Normalize(lastScanAttemptAt.Value),
            MetadataRemoved = string.IsNullOrWhiteSpace(metadataRemoved) ? null : metadataRemoved.Trim()
        };

        return evidence;
    }

    /// <summary>写入扫描结果。Pending 之外的状态是终态，不允许回退或改写。</summary>
    public void MarkScanned(EvidenceScanStatus status, DateTimeOffset now, string? note = null)
    {
        if (status == EvidenceScanStatus.Pending) throw new DomainException("扫描结果不能是待处理。");
        if (ScanStatus != EvidenceScanStatus.Pending) throw new DomainException("凭证的扫描结果已经确定，不能重复写入。");

        var moment = UtcTimestamp.Normalize(now);
        ScanStatus = status;
        ScannedAt = moment;
        ScanAttempts++;
        LastScanAttemptAt = moment;
        LastScanNote = NormalizeNote(note);
    }

    /// <summary>
    /// 记录一次“扫描还没给出结论”的尝试：凭证保持待扫描、继续不可下载，
    /// 但记录次数与原因，便于退避重试和事后排查。
    /// </summary>
    public void RecordScanAttempt(string? note, DateTimeOffset now)
    {
        if (ScanStatus != EvidenceScanStatus.Pending) throw new DomainException("只有待扫描的凭证需要重新扫描。");

        var moment = UtcTimestamp.Normalize(now);
        ScanAttempts++;
        LastScanAttemptAt = moment;
        LastScanNote = NormalizeNote(note);
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

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var trimmed = note.Trim();
        return trimmed.Length <= MaxScanNoteLength ? trimmed : trimmed[..MaxScanNoteLength];
    }

    public static string ExtensionFor(string contentType) =>
        AllowedContentTypes.TryGetValue(NormalizeContentType(contentType), out var extension) ? extension : "bin";
}
