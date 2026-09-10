using System.Security.Cryptography;
using AIToHuman.Contracts.Orders;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

/// <summary>
/// 执行凭证：只有订单服务者可以上传，只有订单双方可以列出与下载，且必须通过安全检查。
/// 声明的大小与真实字节数必须一致，防止客户端谎报大小绕过限额。
/// </summary>
public sealed class EvidenceService(
    IOrderRepository orderRepository,
    IEvidenceRepository evidenceRepository,
    IFileStorage storage,
    IEvidenceScanner scanner,
    TimeProvider timeProvider)
{
    /// <summary>上传上限；API 层会先用这个值拦截超大请求，避免把大文件读进内存。</summary>
    public const long MaxUploadBytes = OrderEvidence.MaxSizeBytes;

    public async Task<EvidenceResponse> UploadAsync(
        Guid orderId,
        Guid uploaderId,
        string fileName,
        string contentType,
        long declaredSize,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        if (order.WorkerId != uploaderId) throw new UnauthorizedAccessException("只有订单服务者可以上传执行凭证。");
        if (order.Status is not (OrderStatus.InProgress or OrderStatus.Submitted))
            throw new DomainException("只有执行中或待验收的订单可以上传凭证。");
        if (evidenceRepository.CountByOrder(orderId) >= OrderEvidence.MaxPerOrder)
            throw new DomainException($"每个订单最多上传 {OrderEvidence.MaxPerOrder} 份凭证。");

        // 先按声明值做便宜校验，再把内容读进有界缓冲区并核对真实长度与摘要。
        OrderEvidence.EnsureSupportedContentType(contentType);
        var (bytes, hash) = await ReadAllAsync(content, cancellationToken);
        if (bytes.Length > MaxUploadBytes) throw new DomainException($"凭证大小不能超过 {MaxUploadBytes / 1024 / 1024} MB。");
        if (declaredSize != bytes.Length) throw new DomainException("声明的文件大小与实际内容不一致，请重新上传。");
        // 不信客户端声明的 MIME，按文件签名再核对一次。
        OrderEvidence.EnsureContentMatchesType(contentType, bytes);

        var evidence = new OrderEvidence(orderId, uploaderId, fileName, OrderEvidence.NormalizeContentType(contentType), bytes.Length, hash, timeProvider.GetUtcNow());
        await storage.SaveAsync(evidence.StorageKey, new MemoryStream(bytes), evidence.ContentType, cancellationToken);

        var scanStatus = await scanner.ScanAsync(evidence.StorageKey, evidence.ContentType, cancellationToken);
        if (scanStatus == EvidenceScanStatus.Rejected)
        {
            // 未通过安全检查的内容不保留，也不落库。
            await storage.DeleteAsync(evidence.StorageKey, cancellationToken);
            throw new DomainException("凭证未通过安全检查，已拒绝保存。");
        }

        evidence.MarkScanned(scanStatus, timeProvider.GetUtcNow());
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

    private Order EnsureParticipant(Guid orderId, Guid viewerId)
    {
        var order = orderRepository.Get(orderId) ?? throw new KeyNotFoundException("订单不存在。");
        if (order.OwnerId != viewerId && order.WorkerId != viewerId) throw new UnauthorizedAccessException("只有订单参与者可以访问执行凭证。");
        return order;
    }

    private static async Task<(byte[] Bytes, string Hash)> ReadAllAsync(Stream content, CancellationToken cancellationToken)
    {
        // 读取时按上限加 1 字节截断，避免恶意大文件把内存吃满。
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read <= 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxUploadBytes) throw new DomainException($"凭证大小不能超过 {MaxUploadBytes / 1024 / 1024} MB。");
        }

        var bytes = buffer.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static EvidenceResponse Map(OrderEvidence evidence) => new(
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
        evidence.IsDownloadable);
}
