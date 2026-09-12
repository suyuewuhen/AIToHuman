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
    bool ScanExhausted);
