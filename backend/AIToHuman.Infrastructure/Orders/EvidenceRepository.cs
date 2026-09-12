using AIToHuman.Application.Orders;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AIToHuman.Infrastructure.Orders;

public sealed class EfEvidenceRepository(TaskDbContext db) : IEvidenceRepository
{
    public OrderEvidence? Get(Guid id) => db.Evidence.SingleOrDefault(item => item.Id == id) is { } record ? Map(record) : null;

    public IReadOnlyCollection<OrderEvidence> ListByOrder(Guid orderId) => db.Evidence
        .AsNoTracking()
        .Where(item => item.OrderId == orderId)
        .OrderBy(item => item.CreatedAt)
        .ThenBy(item => item.Id)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public int CountByOrder(Guid orderId) => db.Evidence.AsNoTracking().Count(item => item.OrderId == orderId);

    public int CountByUploaderSince(Guid uploaderId, DateTimeOffset since) =>
        db.Evidence.AsNoTracking().Count(item => item.UploadedBy == uploaderId && item.CreatedAt >= since);

    public IReadOnlyCollection<OrderEvidence> ListPendingScans(int limit) => db.Evidence
        .AsNoTracking()
        .Where(item => item.ScanStatus == nameof(EvidenceScanStatus.Pending))
        .OrderBy(item => item.CreatedAt)
        .ThenBy(item => item.Id)
        .Take(limit)
        .AsEnumerable()
        .Select(Map)
        .ToArray();

    public void Add(OrderEvidence evidence)
    {
        db.Evidence.Add(ToRecord(evidence));
        db.SaveChanges();
    }

    public void Save(OrderEvidence evidence)
    {
        var record = db.Evidence.Single(item => item.Id == evidence.Id);
        record.ScanStatus = evidence.ScanStatus.ToString();
        record.ScannedAt = evidence.ScannedAt;
        record.ScanAttempts = evidence.ScanAttempts;
        record.LastScanNote = evidence.LastScanNote;
        record.LastScanAttemptAt = evidence.LastScanAttemptAt;
        db.SaveChanges();
    }

    private static EvidenceRecord ToRecord(OrderEvidence evidence) => new()
    {
        Id = evidence.Id,
        OrderId = evidence.OrderId,
        UploadedBy = evidence.UploadedBy,
        FileName = evidence.FileName,
        ContentType = evidence.ContentType,
        StorageKey = evidence.StorageKey,
        SizeBytes = evidence.SizeBytes,
        ContentHash = evidence.ContentHash,
        CreatedAt = evidence.CreatedAt,
        ScanStatus = evidence.ScanStatus.ToString(),
        ScannedAt = evidence.ScannedAt,
        ScanAttempts = evidence.ScanAttempts,
        LastScanNote = evidence.LastScanNote,
        LastScanAttemptAt = evidence.LastScanAttemptAt,
        MetadataRemoved = evidence.MetadataRemoved
    };

    private static OrderEvidence Map(EvidenceRecord record) => OrderEvidence.Rehydrate(
        record.Id,
        record.OrderId,
        record.UploadedBy,
        record.FileName,
        record.ContentType,
        record.StorageKey,
        record.SizeBytes,
        record.ContentHash,
        record.CreatedAt,
        Enum.TryParse<EvidenceScanStatus>(record.ScanStatus, out var status) ? status : EvidenceScanStatus.Pending,
        record.ScannedAt,
        record.ScanAttempts,
        record.LastScanNote,
        record.LastScanAttemptAt,
        record.MetadataRemoved);
}

public sealed class InMemoryEvidenceRepository : IEvidenceRepository
{
    private readonly List<OrderEvidence> evidence = [];
    private readonly Lock gate = new();

    public OrderEvidence? Get(Guid id) { lock (gate) { return evidence.SingleOrDefault(item => item.Id == id); } }

    public IReadOnlyCollection<OrderEvidence> ListByOrder(Guid orderId)
    {
        lock (gate)
        {
            return evidence.Where(item => item.OrderId == orderId).OrderBy(item => item.CreatedAt).ToArray();
        }
    }

    public int CountByOrder(Guid orderId) { lock (gate) { return evidence.Count(item => item.OrderId == orderId); } }

    public int CountByUploaderSince(Guid uploaderId, DateTimeOffset since)
    {
        lock (gate)
        {
            return evidence.Count(item => item.UploadedBy == uploaderId && item.CreatedAt >= since);
        }
    }

    public IReadOnlyCollection<OrderEvidence> ListPendingScans(int limit)
    {
        lock (gate)
        {
            return evidence
                .Where(item => item.ScanStatus == EvidenceScanStatus.Pending)
                .OrderBy(item => item.CreatedAt)
                .ThenBy(item => item.Id)
                .Take(limit)
                .ToArray();
        }
    }

    public void Add(OrderEvidence item) { lock (gate) { evidence.Add(item); } }

    public void Save(OrderEvidence item) { /* 内存实现保存的是同一个对象引用。 */ }
}
