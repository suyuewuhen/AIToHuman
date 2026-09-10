using AIToHuman.Domain.Orders;

namespace AIToHuman.Application.Orders;

public interface IEvidenceRepository
{
    OrderEvidence? Get(Guid id);
    IReadOnlyCollection<OrderEvidence> ListByOrder(Guid orderId);
    int CountByOrder(Guid orderId);
    void Add(OrderEvidence evidence);
    void Save(OrderEvidence evidence);
}

/// <summary>
/// 凭证安全检查。真实实现应接入病毒扫描/内容检测服务；
/// 当前提供的 <c>NoOpEvidenceScanner</c> 只做“未接入扫描”的显式放行，并会打警告日志。
/// </summary>
public interface IEvidenceScanner
{
    Task<EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default);
}
