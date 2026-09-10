using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Domain.Tests;

public sealed class OrderEvidenceTests
{
    private readonly DateTimeOffset _now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
    private readonly Guid _order = Guid.NewGuid();
    private readonly Guid _worker = Guid.NewGuid();

    [Fact]
    public void Evidence_starts_pending_and_uses_a_system_generated_storage_key()
    {
        var evidence = Create();

        Assert.Equal(EvidenceScanStatus.Pending, evidence.ScanStatus);
        Assert.False(evidence.IsDownloadable);
        Assert.Null(evidence.ScannedAt);
        Assert.StartsWith($"{_order:N}/", evidence.StorageKey, StringComparison.Ordinal);
        Assert.EndsWith(".png", evidence.StorageKey, StringComparison.Ordinal);
        // 原始文件名只是展示元数据，绝不参与存储键。
        Assert.DoesNotContain("..", evidence.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain("截图", evidence.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_type_is_normalized_and_whitelisted()
    {
        Assert.Equal("image/png", OrderEvidence.NormalizeContentType("  IMAGE/PNG ; charset=binary  "));
        Assert.True(OrderEvidence.IsSupportedContentType("application/pdf"));
        Assert.False(OrderEvidence.IsSupportedContentType("application/octet-stream"));
        Assert.False(OrderEvidence.IsSupportedContentType("text/html"));
        Assert.False(OrderEvidence.IsSupportedContentType(null));

        var error = Assert.Throws<DomainException>(() => new OrderEvidence(_order, _worker, "x.txt", "text/plain", 10, "hash", _now));
        Assert.Contains("凭证类型不受支持", error.Message);
        Assert.Throws<DomainException>(() => OrderEvidence.EnsureSupportedContentType("image/svg+xml"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(OrderEvidence.MaxSizeBytes + 1)]
    public void Size_must_be_within_the_limit(long size)
    {
        Assert.Throws<DomainException>(() => new OrderEvidence(_order, _worker, "a.png", "image/png", size, "hash", _now));
    }

    [Fact]
    public void Required_fields_are_validated()
    {
        Assert.Throws<DomainException>(() => new OrderEvidence(Guid.Empty, _worker, "a.png", "image/png", 10, "hash", _now));
        Assert.Throws<DomainException>(() => new OrderEvidence(_order, Guid.Empty, "a.png", "image/png", 10, "hash", _now));
        Assert.Throws<DomainException>(() => new OrderEvidence(_order, _worker, "a.png", "image/png", 10, " ", _now));
        Assert.Throws<DomainException>(() => new OrderEvidence(_order, _worker, new string('n', 201), "image/png", 10, "hash", _now));
    }

    [Fact]
    public void Empty_file_name_falls_back_to_a_generated_one()
    {
        var evidence = new OrderEvidence(_order, _worker, "   ", "image/jpeg", 10, "hash", _now);

        Assert.Equal("evidence.jpg", evidence.FileName);
    }

    [Fact]
    public void Scan_result_is_terminal_and_gates_downloads()
    {
        var evidence = Create();

        evidence.MarkScanned(EvidenceScanStatus.Clean, _now.AddMinutes(1));

        Assert.True(evidence.IsDownloadable);
        Assert.Equal(_now.AddMinutes(1), evidence.ScannedAt);
        // 结果已经确定，不能重复写入或回退。
        Assert.Throws<DomainException>(() => evidence.MarkScanned(EvidenceScanStatus.Rejected, _now.AddMinutes(2)));
        Assert.Throws<DomainException>(() => evidence.MarkScanned(EvidenceScanStatus.Pending, _now.AddMinutes(2)));
    }

    [Fact]
    public void Rejected_evidence_is_not_downloadable()
    {
        var evidence = Create();

        evidence.MarkScanned(EvidenceScanStatus.Rejected, _now);

        Assert.False(evidence.IsDownloadable);
    }

    [Fact]
    public void Rehydrate_keeps_storage_key_and_status()
    {
        var id = Guid.NewGuid();
        var evidence = OrderEvidence.Rehydrate(id, _order, _worker, "截图.png", "image/png", "custom/key.png", 1234, "hash", _now, EvidenceScanStatus.Clean, _now.AddMinutes(3));

        Assert.Equal(id, evidence.Id);
        Assert.Equal("custom/key.png", evidence.StorageKey);
        Assert.True(evidence.IsDownloadable);
        Assert.Equal(_now.AddMinutes(3), evidence.ScannedAt);
    }

    private OrderEvidence Create() => new(_order, _worker, "取件码截图.png", "image/png", 2048, "abc123", _now);
}
