using System.Security.Cryptography;
using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Orders;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

/// <summary>
/// 执行凭证：只有订单服务者可以上传，只有订单双方可以列出与下载，且必须通过安全检查。
/// 上传限额来自运营配置（只能在领域硬上限内收紧）；扫描没有给出结论时凭证保持“待扫描、不可下载”，
/// 由后台任务按退避重新扫描，直到给出结论或用完尝试次数。
/// </summary>
public sealed class EvidenceService(
    IOrderRepository orderRepository,
    IEvidenceRepository evidenceRepository,
    IFileStorage storage,
    IEvidenceScanner scanner,
    ISettingsProvider settings,
    TimeProvider timeProvider)
{
    /// <summary>代码默认的单份上限；实际生效值由 <see cref="Limits"/> 解析。</summary>
    public const long DefaultMaxUploadBytes = OrderEvidence.MaxSizeBytes;

    /// <summary>两次重新扫描之间的最短间隔，避免扫描服务刚恢复就被打满。</summary>
    public static readonly TimeSpan RescanBackoff = TimeSpan.FromSeconds(30);

    /// <summary>一轮后台重扫最多处理多少条。</summary>
    public const int RescanBatchSize = 20;

    /// <summary>直连下载地址的默认有效期（秒）；运营可以用 <c>evidence.downloadUrlLifetimeSeconds</c> 调整。</summary>
    public const int DefaultDownloadUrlLifetimeSeconds = 120;

    /// <summary>有效期允许的范围：太短会让大文件下载一半就失效，太长等于把凭证长时间暴露在 URL 里。</summary>
    public const int MinDownloadUrlLifetimeSeconds = 5;
    public const int MaxDownloadUrlLifetimeSeconds = 900;

    /// <summary>当前存储是否支持短时直连下载（本机目录不支持）。</summary>
    public bool SupportsDirectDownload => storage is IPresignedFileStorage { SupportsPresignedDownload: true };

    /// <summary>
    /// 当前生效的上传限额。运营填的值会被裁剪到领域硬上限之内：
    /// 写坏一个数字不该让上传彻底不可用，但也不允许突破硬上限。
    /// </summary>
    public EvidenceLimits Limits() => EvidenceLimits.Create(
        Math.Clamp(
            settings.GetInt(SettingKeys.EvidenceMaxSizeBytes) ?? OrderEvidence.MaxSizeBytes,
            EvidenceLimits.MinSizeBytes,
            OrderEvidence.AbsoluteMaxSizeBytes),
        Math.Clamp(
            settings.GetInt(SettingKeys.EvidenceMaxPerOrder) ?? OrderEvidence.MaxPerOrder,
            1,
            OrderEvidence.AbsoluteMaxPerOrder));

    public async Task<EvidenceResponse> UploadAsync(
        Guid orderId,
        Guid uploaderId,
        string fileName,
        string contentType,
        long declaredSize,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var limits = Limits();
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        if (order.WorkerId != uploaderId) throw new UnauthorizedAccessException("只有订单服务者可以上传执行凭证。");
        if (order.Status is not (OrderStatus.InProgress or OrderStatus.Submitted))
            throw new DomainException("只有执行中或待验收的订单可以上传凭证。");
        limits.EnsureCountWithin(evidenceRepository.CountByOrder(orderId));

        // 先按声明值做便宜校验，再把内容读进有界缓冲区并核对真实长度与摘要。
        OrderEvidence.EnsureSupportedContentType(contentType);
        var (bytes, hash) = await ReadAllAsync(content, limits, cancellationToken);
        if (declaredSize != bytes.Length) throw new DomainException("声明的文件大小与实际内容不一致，请重新上传。");
        // 不信客户端声明的 MIME，按文件签名再核对一次。
        OrderEvidence.EnsureContentMatchesType(contentType, bytes);

        var now = timeProvider.GetUtcNow();
        var evidence = new OrderEvidence(orderId, uploaderId, fileName, OrderEvidence.NormalizeContentType(contentType), bytes.Length, hash, now, limits);
        await storage.SaveAsync(evidence.StorageKey, new MemoryStream(bytes), evidence.ContentType, cancellationToken);

        var scanStatus = await scanner.ScanAsync(evidence.StorageKey, evidence.ContentType, cancellationToken);
        if (scanStatus == EvidenceScanStatus.Rejected)
        {
            // 未通过安全检查的内容不保留，也不落库。
            await storage.DeleteAsync(evidence.StorageKey, cancellationToken);
            throw new DomainException("凭证未通过安全检查，已拒绝保存。");
        }

        if (scanStatus == EvidenceScanStatus.Pending)
        {
            // 扫描服务没给出结论：凭证保留但不可下载，等后台重扫。
            evidence.RecordScanAttempt("上传后检查没有给出结论，稍后自动重试。", now);
        }
        else
        {
            evidence.MarkScanned(scanStatus, now, "上传后检查通过。");
        }

        evidenceRepository.Add(evidence);
        return Map(evidence);
    }

    public IReadOnlyCollection<EvidenceResponse> List(Guid orderId, Guid viewerId)
    {
        EnsureParticipant(orderId, viewerId);
        return evidenceRepository.ListByOrder(orderId).Select(Map).ToArray();
    }

    /// <summary>下载前重新校验当前用户与订单的关系，并对未通过扫描的凭证返回 403。</summary>
    public async Task<(EvidenceResponse Evidence, Stream Content)> DownloadAsync(Guid evidenceId, Guid viewerId, CancellationToken cancellationToken = default)
    {
        var evidence = evidenceRepository.Get(evidenceId) ?? throw new KeyNotFoundException("凭证不存在。");
        EnsureParticipant(evidence.OrderId, viewerId);
        if (!evidence.IsDownloadable) throw new UnauthorizedAccessException("凭证尚未通过安全检查，暂不可下载。");

        var content = await storage.OpenReadAsync(evidence.StorageKey, cancellationToken)
            ?? throw new KeyNotFoundException("凭证文件不存在。");
        return (Map(evidence), content);
    }

    /// <summary>
    /// 签发短时直连下载地址。权限与扫描状态的判定和流式下载完全一致：
    /// 只有订单参与者、且凭证已通过检查才能拿到地址；地址本身在有效期内可被持有者直接访问，
    /// 因此有效期由运营配置控制，默认 120 秒。
    /// </summary>
    public EvidenceDownloadUrlResponse CreateDownloadUrl(Guid evidenceId, Guid viewerId)
    {
        var evidence = evidenceRepository.Get(evidenceId) ?? throw new KeyNotFoundException("凭证不存在。");
        EnsureParticipant(evidence.OrderId, viewerId);
        if (!evidence.IsDownloadable) throw new UnauthorizedAccessException("凭证尚未通过安全检查，暂不可下载。");
        if (storage is not IPresignedFileStorage presigned)
            throw new DomainException("当前存储不支持短时直连下载地址：请改用鉴权后的 /content 接口下载。");

        var seconds = Math.Clamp(
            settings.GetInt(SettingKeys.EvidenceDownloadUrlLifetimeSeconds) ?? DefaultDownloadUrlLifetimeSeconds,
            MinDownloadUrlLifetimeSeconds,
            MaxDownloadUrlLifetimeSeconds);

        var fileName = $"evidence-{evidence.Id:N}.{OrderEvidence.ExtensionFor(evidence.ContentType)}";
        var download = presigned.CreatePresignedDownload(evidence.StorageKey, TimeSpan.FromSeconds(seconds), fileName);
        return new EvidenceDownloadUrlResponse(download.Url, download.ExpiresAt);
    }

    /// <summary>
    /// 重新扫描长期停留在“待扫描”的凭证（扫描服务不可用且 failMode=closed 时会出现）。
    /// 返回这一轮处理了多少条；已经用完尝试次数的凭证会被跳过，保留给人工处理。
    /// </summary>
    public async Task<int> RescanPendingAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var handled = 0;

        foreach (var evidence in evidenceRepository.ListPendingScans(RescanBatchSize))
        {
            if (!evidence.CanRetryScan) continue;
            // 退避：刚试过的不再重复打扫描服务。
            if (evidence.LastScanAttemptAt is { } lastAttempt && now - lastAttempt < RescanBackoff) continue;

            if (!await storage.ExistsAsync(evidence.StorageKey, cancellationToken))
            {
                // 文件已经不在了，再重试也没有意义：直接判定为不可用，避免永远挂在待扫描。
                evidence.MarkScanned(EvidenceScanStatus.Rejected, now, "重新扫描时找不到文件，已标记为未通过。");
                evidenceRepository.Save(evidence);
                handled++;
                continue;
            }

            try
            {
                var status = await scanner.ScanAsync(evidence.StorageKey, evidence.ContentType, cancellationToken);
                switch (status)
                {
                    case EvidenceScanStatus.Clean:
                        evidence.MarkScanned(status, now, "重新扫描通过。");
                        break;

                    case EvidenceScanStatus.Rejected:
                        // 未通过的内容不再留在存储里。
                        await storage.DeleteAsync(evidence.StorageKey, cancellationToken);
                        evidence.MarkScanned(status, now, "重新扫描未通过安全检查。");
                        break;

                    default:
                        evidence.RecordScanAttempt(DescribeAttempt(evidence, "扫描服务仍未给出结论。"), now);
                        break;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                evidence.RecordScanAttempt(DescribeAttempt(evidence, $"扫描失败：{exception.Message}"), now);
            }

            evidenceRepository.Save(evidence);
            handled++;
        }

        return handled;
    }

    private Order EnsureParticipant(Guid orderId, Guid viewerId)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        if (order.OwnerId != viewerId && order.WorkerId != viewerId) throw new UnauthorizedAccessException("只有订单参与者可以访问执行凭证。");
        return order;
    }

    /// <summary>把“这是最后一次自动重试”写进说明，免得人工看到一条不再重试却没有任何解释的记录。</summary>
    private static string DescribeAttempt(OrderEvidence evidence, string note) =>
        evidence.ScanAttempts + 1 >= OrderEvidence.MaxScanAttempts
            ? $"{note}（已尝试 {OrderEvidence.MaxScanAttempts} 次，停止自动重试，请人工处理）"
            : note;

    private static async Task<(byte[] Bytes, string Hash)> ReadAllAsync(Stream content, EvidenceLimits limits, CancellationToken cancellationToken)
    {
        // 读取时按上限加 1 字节截断，避免恶意大文件把内存吃满。
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limits.MaxSizeBytes) throw new DomainException($"凭证大小不能超过 {limits.MaxSizeDisplay}。");
        }

        var bytes = buffer.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private EvidenceResponse Map(OrderEvidence evidence) => new(
        evidence.Id,
        evidence.OrderId,
        evidence.UploadedBy,
        evidence.FileName,
        evidence.ContentType,
        evidence.SizeBytes,
        evidence.ContentHash,
        evidence.ScanStatus.ToString(),
        evidence.CreatedAt,
        evidence.ScannedAt,
        evidence.IsDownloadable,
        evidence.ScanAttempts,
        evidence.LastScanNote,
        evidence.ScanExhausted,
        SupportsDirectDownload);
}
