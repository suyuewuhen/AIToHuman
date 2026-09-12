using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

public interface IEvidenceRepository
{
    OrderEvidence? Get(Guid id);
    IReadOnlyCollection<OrderEvidence> ListByOrder(Guid orderId);
    int CountByOrder(Guid orderId);

    /// <summary>按上传时间正序取出还在等待扫描的凭证，供后台重新扫描使用。</summary>
    IReadOnlyCollection<OrderEvidence> ListPendingScans(int limit);

    void Add(OrderEvidence evidence);
    void Save(OrderEvidence evidence);
}

/// <summary>
/// 凭证安全检查。真实实现应接入病毒扫描/内容检测服务；
/// 当前由 <c>HttpEvidenceScanner</c> 实现：<c>provider=none</c> 时显式放行并打警告日志，
/// <c>provider=http</c> 时调用外部扫描服务，失败按 <c>failMode</c> 决定放行还是保持待扫描。
/// </summary>
public interface IEvidenceScanner
{
    Task<EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default);
}
