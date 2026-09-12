using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using AIToHuman.Contracts.Orders;
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

    [Fact]
    public async Task Configured_size_limit_is_enforced_before_the_hard_ceiling()
    {
        var world = new EvidenceWorld(settings: new Dictionary<string, string> { [SettingKeys.EvidenceMaxSizeBytes] = "1024" });
        var order = world.CreateOrder();
        var tooBig = new byte[2048];

        var error = await Assert.ThrowsAsync<DomainException>(() =>
            world.Service.UploadAsync(order.Id, order.WorkerId, "big.png", "image/png", tooBig.Length, new MemoryStream(tooBig)));

        Assert.Contains("凭证大小不能超过 1 KB", error.Message);
        Assert.Empty(world.Storage.Files);
    }

    [Fact]
    public void Configured_limits_are_clamped_to_the_hard_ceiling()
    {
        var world = new EvidenceWorld(settings: new Dictionary<string, string>
        {
            [SettingKeys.EvidenceMaxSizeBytes] = "999999999",
            [SettingKeys.EvidenceMaxPerOrder] = "9999"
        });

        var limits = world.Service.Limits();

        // 写坏一个数字不该让上传彻底不可用，但也绝不允许突破硬上限。
        Assert.Equal(OrderEvidence.AbsoluteMaxSizeBytes, limits.MaxSizeBytes);
        Assert.Equal(OrderEvidence.AbsoluteMaxPerOrder, limits.MaxPerOrder);
    }

    [Fact]
    public async Task Configured_count_limit_is_enforced()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Clean, new Dictionary<string, string> { [SettingKeys.EvidenceMaxPerOrder] = "1" });
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);

        var error = await Assert.ThrowsAsync<DomainException>(() => world.UploadPendingAsync(order));

        Assert.Equal("每个订单最多上传 1 份凭证。", error.Message);
    }

    [Fact]
    public async Task Pending_upload_stays_undownloadable_and_records_an_attempt()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();

        var uploaded = await world.UploadPendingAsync(order);

        Assert.Equal("Pending", uploaded.ScanStatus);
        Assert.False(uploaded.IsDownloadable);
        Assert.Equal(1, uploaded.ScanAttempts);
        Assert.False(uploaded.ScanExhausted);
        Assert.Contains("稍后自动重试", uploaded.LastScanNote);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => world.Service.DownloadAsync(uploaded.Id, order.OwnerId));
    }

    [Fact]
    public async Task Rescan_marks_previously_pending_evidence_clean()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);

        world.Scanner.Status = EvidenceScanStatus.Clean;
        world.PassRescanBackoff();

        Assert.Equal(1, await world.Service.RescanPendingAsync());

        var item = world.Single(order);
        Assert.Equal(EvidenceScanStatus.Clean, item.ScanStatus);
        Assert.True(item.IsDownloadable);
        Assert.Equal(2, item.ScanAttempts);
        Assert.Equal("重新扫描通过。", item.LastScanNote);

        // 已经有结论的凭证不会再进重扫队列。
        world.PassRescanBackoff();
        Assert.Equal(0, await world.Service.RescanPendingAsync());
    }

    [Fact]
    public async Task Rescan_rejects_and_removes_the_file_when_the_scanner_rejects()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);
        var storageKey = world.Single(order).StorageKey;

        world.Scanner.Status = EvidenceScanStatus.Rejected;
        world.PassRescanBackoff();
        await world.Service.RescanPendingAsync();

        Assert.Equal(EvidenceScanStatus.Rejected, world.Single(order).ScanStatus);
        Assert.DoesNotContain(storageKey, world.Storage.Files.Keys);
    }

    [Fact]
    public async Task Rescan_marks_missing_files_as_rejected()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);
        world.Storage.Files.Clear();

        world.PassRescanBackoff();
        await world.Service.RescanPendingAsync();

        var item = world.Single(order);
        Assert.Equal(EvidenceScanStatus.Rejected, item.ScanStatus);
        Assert.Contains("找不到文件", item.LastScanNote);
    }

    [Fact]
    public async Task Rescan_skips_items_that_were_just_attempted()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);

        // 退避窗口内不重复打扫描服务。
        Assert.Equal(0, await world.Service.RescanPendingAsync());
        world.PassRescanBackoff();
        Assert.Equal(1, await world.Service.RescanPendingAsync());
    }

    [Fact]
    public async Task Rescan_stops_after_the_maximum_attempts()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);

        for (var round = 0; round < OrderEvidence.MaxScanAttempts + 3; round++)
        {
            world.PassRescanBackoff();
            await world.Service.RescanPendingAsync();
        }

        var item = world.Single(order);
        Assert.Equal(OrderEvidence.MaxScanAttempts, item.ScanAttempts);
        Assert.True(item.ScanExhausted);
        Assert.False(item.CanRetryScan);
        Assert.Contains("停止自动重试", item.LastScanNote);
        Assert.Equal(EvidenceScanStatus.Pending, item.ScanStatus);

        world.PassRescanBackoff();
        Assert.Equal(0, await world.Service.RescanPendingAsync());
    }

    [Fact]
    public async Task Rescan_counts_transport_failures_against_the_attempt_budget()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending);
        var order = world.CreateOrder();
        await world.UploadPendingAsync(order);

        world.Scanner.Failure = new InvalidOperationException("扫描服务挂了");
        world.PassRescanBackoff();
        await world.Service.RescanPendingAsync();

        var item = world.Single(order);
        Assert.Equal(2, item.ScanAttempts);
        Assert.Contains("扫描失败", item.LastScanNote);
        Assert.Equal(EvidenceScanStatus.Pending, item.ScanStatus);
    }

    [Fact]
    public void Local_like_storage_reports_no_direct_download_and_refuses_to_sign()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        var evidence = new OrderEvidence(order.Id, order.WorkerId, "a.png", "image/png", 3, "hash", Now);
        // 先让凭证具备下载资格，才能走到“存储不支持直连”这一层判断。
        evidence.MarkScanned(EvidenceScanStatus.Clean, Now);
        world.Evidence.Add(evidence);

        // 列表里如实标注“不支持直连下载”，让客户端走 /content。
        Assert.False(world.Service.List(order.Id, order.OwnerId).Single().PresignedDownloadAvailable);

        var error = Assert.Throws<DomainException>(() => world.Service.CreateDownloadUrl(evidence.Id, order.OwnerId));
        Assert.Contains("不支持短时直连下载地址", error.Message);
    }

    [Fact]
    public void Scan_gate_is_checked_before_the_storage_capability()
    {
        var world = new EvidenceWorld();
        var order = world.CreateOrder();
        var pending = new OrderEvidence(order.Id, order.WorkerId, "a.png", "image/png", 3, "hash", Now);
        world.Evidence.Add(pending);

        // 未通过检查时先报扫描门禁，不泄露“存储是否支持直连”这类实现细节。
        var error = Assert.Throws<UnauthorizedAccessException>(() => world.Service.CreateDownloadUrl(pending.Id, order.OwnerId));
        Assert.Contains("尚未通过安全检查", error.Message);
    }

    [Fact]
    public async Task Direct_download_url_needs_a_participant_and_a_clean_scan()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Clean, presigned: true);
        var order = world.CreateOrder();
        var uploaded = await world.UploadPendingAsync(order);
        Assert.True(world.Service.SupportsDirectDownload);
        Assert.True(world.Service.List(order.Id, order.OwnerId).Single().PresignedDownloadAvailable);

        Assert.Throws<UnauthorizedAccessException>(() => world.Service.CreateDownloadUrl(uploaded.Id, Guid.NewGuid()));

        var url = world.Service.CreateDownloadUrl(uploaded.Id, order.OwnerId);
        Assert.StartsWith("https://storage.example.com/", url.Url);
        Assert.Equal(Now.AddSeconds(EvidenceService.DefaultDownloadUrlLifetimeSeconds), url.ExpiresAt);
        // 下载文件名由系统生成，不用用户原始文件名。
        var request = Assert.Single(world.Presigned!.Requests);
        Assert.EndsWith(".png", request.FileName);
        Assert.Equal($"evidence-{uploaded.Id:N}.png", request.FileName);
    }

    [Fact]
    public async Task Direct_download_url_is_refused_while_the_scan_has_no_verdict()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Pending, presigned: true);
        var order = world.CreateOrder();
        var uploaded = await world.UploadPendingAsync(order);

        var error = Assert.Throws<UnauthorizedAccessException>(() => world.Service.CreateDownloadUrl(uploaded.Id, order.OwnerId));

        Assert.Contains("尚未通过安全检查", error.Message);
        Assert.Empty(world.Presigned!.Requests);
    }

    [Fact]
    public async Task Direct_download_url_lifetime_comes_from_settings()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Clean, presigned: true, settings: new Dictionary<string, string>
        {
            [SettingKeys.EvidenceDownloadUrlLifetimeSeconds] = "30"
        });
        var order = world.CreateOrder();
        var uploaded = await world.UploadPendingAsync(order);

        Assert.Equal(Now.AddSeconds(30), world.Service.CreateDownloadUrl(uploaded.Id, order.OwnerId).ExpiresAt);
    }

    [Fact]
    public async Task Direct_download_url_lifetime_is_clamped()
    {
        var world = new EvidenceWorld(EvidenceScanStatus.Clean, presigned: true, settings: new Dictionary<string, string>
        {
            [SettingKeys.EvidenceDownloadUrlLifetimeSeconds] = "1"
        });
        var order = world.CreateOrder();
        var uploaded = await world.UploadPendingAsync(order);

        // 配置写坏了也不能签出“1 秒就过期”的地址。
        Assert.Equal(Now.AddSeconds(EvidenceService.MinDownloadUrlLifetimeSeconds), world.Service.CreateDownloadUrl(uploaded.Id, order.OwnerId).ExpiresAt);
    }

    /// <summary>1x1 透明 PNG（67 字节），用于让文件签名校验通过。</summary>
    private static byte[] PngBytes() =>    [
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

    /// <summary>测试用的凭证环境：真实 EvidenceService + 内存仓储 + 可切换的扫描替身。</summary>
    private sealed class EvidenceWorld
    {
        public EvidenceWorld(
            EvidenceScanStatus scanStatus = EvidenceScanStatus.Clean,
            Dictionary<string, string>? settings = null,
            MutableEvidenceScanner? scanner = null,
            bool presigned = false)
        {
            Clock = new MutableTimeProvider(Now);
            Storage = new InMemoryFileStorage();
            // presigned=true 时改用支持签发直连地址的替身，字节仍走同一个内存存储。
            Presigned = presigned ? new FakePresignedFileStorage(Storage, Clock) : null;
            Evidence = new InMemoryEvidenceRepository();
            Orders = new InMemoryOrderRepository();
            Scanner = scanner ?? new MutableEvidenceScanner(scanStatus);
            Settings = new StubSettingsProvider(settings ?? new Dictionary<string, string>());
            Service = new EvidenceService(Orders, Evidence, (IFileStorage?)Presigned ?? Storage, Scanner, Settings, Clock);
        }

        public MutableTimeProvider Clock { get; }
        public InMemoryFileStorage Storage { get; }

        /// <summary>仅在 presigned=true 时存在；用于断言签发请求与有效期。</summary>
        public FakePresignedFileStorage? Presigned { get; }

        public InMemoryEvidenceRepository Evidence { get; }
        public InMemoryOrderRepository Orders { get; }
        public MutableEvidenceScanner Scanner { get; }
        public StubSettingsProvider Settings { get; }
        public EvidenceService Service { get; }

        /// <summary>推进时钟，跨过重新扫描的退避窗口。</summary>
        public void PassRescanBackoff() => Clock.Advance(EvidenceService.RescanBackoff + TimeSpan.FromSeconds(1));

        public Order CreateOrder(bool start = true)
        {
            var order = new Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "代取文件", new Money(50), Now);
            Orders.Add(order);
            if (start) order.Start(order.WorkerId);
            return order;
        }

        public Task<EvidenceResponse> UploadPendingAsync(Order order) =>
            Service.UploadAsync(order.Id, order.WorkerId, "a.png", "image/png", PngBytes().Length, new MemoryStream(PngBytes()));

        public OrderEvidence Single(Order order) => Evidence.ListByOrder(order.Id).Single();
    }
}
