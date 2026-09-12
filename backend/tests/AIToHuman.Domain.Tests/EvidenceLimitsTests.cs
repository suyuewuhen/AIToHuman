using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 上传限额是“运营可调、硬上限不可越”的：这里固定的是边界行为，不是具体数值。
/// </summary>
public sealed class EvidenceLimitsTests
{
    [Fact]
    public void Default_matches_the_code_defaults()
    {
        Assert.Equal(OrderEvidence.MaxSizeBytes, EvidenceLimits.Default.MaxSizeBytes);
        Assert.Equal(OrderEvidence.MaxPerOrder, EvidenceLimits.Default.MaxPerOrder);
        Assert.Equal(OrderEvidence.AbsoluteMaxSizeBytes, EvidenceLimits.Absolute.MaxSizeBytes);
    }

    [Fact]
    public void Operator_can_tighten_the_limits()
    {
        var limits = EvidenceLimits.Create(2 * 1024 * 1024, 3);

        Assert.Equal(2 * 1024 * 1024, limits.MaxSizeBytes);
        Assert.Equal(3, limits.MaxPerOrder);
        Assert.Equal("2 MB", limits.MaxSizeDisplay);
    }

    [Theory]
    [InlineData(0L, 10)]
    [InlineData(EvidenceLimits.MinSizeBytes - 1, 10)]
    [InlineData(OrderEvidence.AbsoluteMaxSizeBytes + 1, 10)]
    [InlineData(5 * 1024 * 1024, 0)]
    [InlineData(5 * 1024 * 1024, OrderEvidence.AbsoluteMaxPerOrder + 1)]
    public void Out_of_range_limits_are_rejected(long maxSizeBytes, int maxPerOrder) =>
        Assert.Throws<DomainException>(() => EvidenceLimits.Create(maxSizeBytes, maxPerOrder));

    [Fact]
    public void Size_display_falls_back_to_kilobytes_for_fractional_megabytes()
    {
        Assert.Equal("1500 KB", EvidenceLimits.Create(1500 * 1024, 10).MaxSizeDisplay);
        Assert.Equal("25 MB", EvidenceLimits.Create(OrderEvidence.AbsoluteMaxSizeBytes, 10).MaxSizeDisplay);
    }

    [Fact]
    public void Ensure_size_and_count_use_the_configured_values()
    {
        var limits = EvidenceLimits.Create(1024, 1);

        Assert.Throws<DomainException>(() => limits.EnsureSizeWithin(1025));
        limits.EnsureSizeWithin(1024);
        limits.EnsureCountWithin(0);
        var error = Assert.Throws<DomainException>(() => limits.EnsureCountWithin(1));
        Assert.Equal("每个订单最多上传 1 份凭证。", error.Message);
    }

    [Fact]
    public void Evidence_construction_respects_passed_limits()
    {
        var limits = EvidenceLimits.Create(1024, 1);

        Assert.Throws<DomainException>(() =>
            new OrderEvidence(Guid.NewGuid(), Guid.NewGuid(), "a.png", "image/png", 2048, "hash", DateTimeOffset.UtcNow, limits));
    }

    [Fact]
    public void Content_type_whitelist_stays_in_code()
    {
        // 类型白名单刻意不做成运营配置：放开它等于允许上传可执行内容。
        Assert.True(OrderEvidence.IsSupportedContentType("image/png"));
        Assert.False(OrderEvidence.IsSupportedContentType("application/x-msdownload"));
    }
}
