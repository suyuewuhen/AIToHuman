using AIToHuman.Application.Orders;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using AIToHuman.Domain.Tasks;
using AIToHuman.Infrastructure.Orders;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// 执行凭证：类型与大小白名单、声明值与真实内容一致性、权限边界、扫描结果门禁，
/// 以及被拒绝的文件不落库也不留在存储里。
/// </summary>
public sealed class EvidenceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Worker_can_upload_and_participants_can_download()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        var content = PngBytes();

        var uploaded = await world.Service.UploadAsync(order.Id, order.WorkerId, "取件码截图.png", "image/png", content.Length, new MemoryStream(content));

        Assert.Equal("Clean", uploaded.ScanStatus);
        Assert.True(uploaded.IsDownloadable);
        Assert.Equal(content.Length, uploaded.SizeBytes);
        Assert.Equal(64, uploaded.ContentHash.Length);
        Assert.Single(world.Storage.Files);
        var storedKey = world.Storage.Files.Keys.Single();
        Assert.DoesNotContain("取件码", storedKey, StringComparison.Ordinal);

        var (evidence, stream) = await world.Service.DownloadAsync(uploaded.Id, order.OwnerId);
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(content, buffer.ToArray());
        }

        Assert.Equal("image/png", evidence.ContentType);
        Assert.Single(world.Service.List(order.Id, order.WorkerId));
    }

    [Fact]
    public async Task Owner_cannot_upload_evidence()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();

        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            world.Service.UploadAsync(order.Id, order.OwnerId, "a.png", "image/png", 3, new MemoryStream([1, 2, 3])));

        Assert.Equal("只有订单服务者可以上传执行凭证。", error.Message);
        Assert.Empty(world.Storage.Files);
    }

    [Fact]
    public async Task Evidence_is_only_accepted_while_the_order_is_running()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder(start: false);

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "a.png", "image/png", PngBytes().Length, new MemoryStream(PngBytes())));

        Assert.Equal("只有执行中或待验收的订单可以上传凭证。", error.Message);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/octet-stream")]
    [InlineData("image/svg+xml")]
    public async Task Unsupported_types_are_rejected_before_reading_the_content(string contentType)
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "payload.txt", contentType, 3, new MemoryStream([1, 2, 3])));

        Assert.Contains("凭证类型不受支持", error.Message);
        Assert.Empty(world.Storage.Files);
    }

    [Fact]
    public async Task Declared_size_must_match_the_real_content()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "a.png", "image/png", 100, new MemoryStream([1, 2, 3])));

        Assert.Equal("声明的文件大小与实际内容不一致，请重新上传。", error.Message);
        Assert.Empty(world.Storage.Files);
        Assert.Empty(world.Evidence.ListByOrder(order.Id));
    }

    [Fact]
    public async Task Content_larger_than_the_limit_is_rejected_even_if_the_declared_size_lies()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        var oversized = new byte[OrderEvidence.MaxSizeBytes + 1];

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "big.png", "image/png", 10, new MemoryStream(oversized)));

        Assert.Contains("凭证大小不能超过", error.Message);
        Assert.Empty(world.Storage.Files);
    }

    [Fact]
    public async Task Rejected_scan_removes_the_file_and_saves_nothing()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Rejected);
        var order = world.CreateOrder();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "a.png", "image/png", PngBytes().Length, new MemoryStream(PngBytes())));

        Assert.Equal("凭证未通过安全检查，已拒绝保存。", error.Message);
        Assert.Empty(world.Storage.Files);
        Assert.Empty(world.Evidence.ListByOrder(order.Id));
    }

    [Fact]
    public async Task Pending_evidence_is_not_downloadable()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        var pending = new OrderEvidence(order.Id, order.WorkerId, "a.png", "image/png", 3, "hash", Now);
        world.Evidence.Add(pending);

        Assert.True(pending.IsDownloadable == false);
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.DownloadAsync(pending.Id, order.OwnerId));
        Assert.Equal("凭证尚未通过安全检查，暂不可下载。", error.Message);
        // 列表里能看到元数据，但标记为不可下载。
        Assert.False(world.Service.List(order.Id, order.OwnerId).Single().IsDownloadable);
    }

    [Fact]
    public async Task Non_participants_cannot_list_or_download()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        var uploaded = await world.Service.UploadAsync(order.Id, order.WorkerId, "a.png", "image/png", PngBytes().Length, new MemoryStream(PngBytes()));
        var outsider = Guid.NewGuid();

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.List(order.Id, outsider));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.DownloadAsync(uploaded.Id, outsider));
    }

    [Fact]
    public async Task Unknown_order_or_evidence_is_reported_as_missing()
    {
        var world = new EvidenceWorld();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            world.Service.UploadAsync(Guid.NewGuid(), Guid.NewGuid(), "a.png", "image/png", 3, new MemoryStream([1, 2, 3])));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => world.Service.DownloadAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Each_order_has_a_bounded_number_of_evidence_items()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        for (var index = 0; index < OrderEvidence.MaxPerOrder; index++)
        {
            await world.Service.UploadAsync(order.Id, order.WorkerId, $"a{index}.png", "image/png", PngBytes().Length, new MemoryStream(PngBytes()));
        }

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "extra.png", "image/png", PngBytes().Length, new MemoryStream(PngBytes())));

        Assert.Equal($"每个订单最多上传 {OrderEvidence.MaxPerOrder} 份凭证。", error.Message);
    }

    [Fact]
    public async Task Content_signature_must_match_the_declared_type()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        // 声明是 PNG，内容却不是：服务端不信 MIME，按文件签名拒绝。
        var fake = "这不是真正的 PNG"u8.ToArray();

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "fake.png", "image/png", fake.Length, new MemoryStream(fake)));

        Assert.Equal("凭证内容与声明的类型不一致，已拒绝保存。", error.Message);
        Assert.Empty(world.Storage.Files);
        Assert.Empty(world.Evidence.ListByOrder(order.Id));
    }

    /// <summary>1x1 透明 PNG（67 字节），用于让文件签名校验通过。</summary>
    private static byte[] PngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82
    ];

    private sealed class EvidenceWorld
    {
        public EvidenceWorld(EvidenceScanStatus scanStatus = EvidenceScanStatus.Clean)
        {
            Clock = new FixedTimeProvider(Now);
            Storage = new InMemoryFileStorage();
            Evidence = new InMemoryEvidenceRepository();
            Orders = new InMemoryOrderRepository();
            Service = new EvidenceService(Orders, Evidence, Storage, new FakeEvidenceScanner(scanStatus), Clock);
        }

        public FixedTimeProvider Clock { get; }
        public InMemoryFileStorage Storage { get; }
        public InMemoryEvidenceRepository Evidence { get; }
        public InMemoryOrderRepository Orders { get; }
        public EvidenceService Service { get; }

        public Order CreateOrder(bool start = true)
        {
            var order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "代取文件", new Money(50), Now);
            Orders.Add(order);
            if (start) order.Start(order.WorkerId);
            return order;
        }
    }
}
