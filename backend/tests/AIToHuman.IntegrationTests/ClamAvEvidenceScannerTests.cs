using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Orders;
using AIToHuman.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToHuman.IntegrationTests;

/// <summary>
/// ClamAV 的 INSTREAM 协议实现：用一个按协议应答的 TCP 替身验证线格式与判定映射。
/// 本机没有部署真实的 clamd，所以这里证明的是「协议往返正确」，
/// 真实引擎的检出能力由部署方自己的病毒库决定。
/// </summary>
public sealed class ClamAvEvidenceScannerTests
{
    private static readonly byte[] Payload = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x11, 0x22, 0x33];

    [Theory]
    [InlineData("stream: OK", EvidenceScanStatus.Clean)]
    [InlineData("stream: Eicar-Test-Signature FOUND", EvidenceScanStatus.Rejected)]
    public async Task Verdicts_are_mapped_from_the_clamd_reply(string reply, EvidenceScanStatus expected)
    {
        var stub = new ClamAvStub(reply);
        var scanner = Build(stub.Start());

        Assert.Equal(expected, await scanner.ScanAsync("order/file.png", "image/png"));

        stub.Stop();
    }

    [Fact]
    public async Task The_file_is_streamed_in_length_prefixed_chunks_over_instream()
    {
        var stub = new ClamAvStub("stream: OK");
        var scanner = Build(stub.Start());

        await scanner.ScanAsync("order/file.png", "image/png");
        var (command, payload) = await stub.Received;

        // z 前缀 = 命令以 NUL 结尾；随后是长度前缀的数据块，最后是零长度块。
        Assert.Equal("zINSTREAM\0", command);
        Assert.Equal(Payload, payload);
    }

    [Fact]
    public async Task Error_reply_follows_the_failure_mode()
    {
        var closed = new ClamAvStub("INSTREAM size limit exceeded. ERROR");
        Assert.Equal(EvidenceScanStatus.Pending, await Build(closed.Start(), failMode: "closed").ScanAsync("order/file.png", "image/png"));
        closed.Stop();

        var open = new ClamAvStub("INSTREAM size limit exceeded. ERROR");
        Assert.Equal(EvidenceScanStatus.Clean, await Build(open.Start(), failMode: "open").ScanAsync("order/file.png", "image/png"));
        open.Stop();
    }

    [Fact]
    public async Task Unreachable_clamd_follows_the_failure_mode()
    {
        // 127.0.0.1 上一个必然没人监听的端口：连接被拒。
        var closedPort = FreePort();
        Assert.Equal(EvidenceScanStatus.Pending, await Build(closedPort, failMode: "closed").ScanAsync("order/file.png", "image/png"));
        Assert.Equal(EvidenceScanStatus.Clean, await Build(closedPort, failMode: "open").ScanAsync("order/file.png", "image/png"));
    }

    [Fact]
    public async Task Missing_file_is_reported_as_unavailable_instead_of_clean()
    {
        var stub = new ClamAvStub("stream: OK");
        var scanner = Build(stub.Start(), withFile: false);

        Assert.Equal(EvidenceScanStatus.Pending, await scanner.ScanAsync("order/missing.png", "image/png"));

        stub.Stop();
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static ClamAvEvidenceScanner Build(int port, string failMode = "closed", bool withFile = true)
    {
        var storage = new InMemoryFileStorage();
        if (withFile) storage.Files["order/file.png"] = Payload;

        var settings = new StubSettingsProvider(new Dictionary<string, string>
        {
            [SettingKeys.EvidenceScannerClamAvHost] = "127.0.0.1",
            [SettingKeys.EvidenceScannerClamAvPort] = port.ToString(),
            [SettingKeys.EvidenceScannerTimeoutSeconds] = "5",
            [SettingKeys.EvidenceScannerFailMode] = failMode
        });

        return new ClamAvEvidenceScanner(settings, storage, NullLogger<ClamAvEvidenceScanner>.Instance);
    }

    /// <summary>按 INSTREAM 协议应答一次的 TCP 替身，同时记录收到的命令与载荷。</summary>
    private sealed class ClamAvStub(string reply)
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly TaskCompletionSource<(string Command, byte[] Payload)> received = new();

        public int Start()
        {
            listener.Start();
            _ = AcceptAsync();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public Task<(string Command, byte[] Payload)> Received => received.Task;

        public void Stop()
        {
            cancellation.Cancel();
            listener.Stop();
        }

        private async Task AcceptAsync()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                await using var stream = client.GetStream();

                var command = new List<byte>();
                var single = new byte[1];
                while (await stream.ReadAsync(single, cancellation.Token) == 1)
                {
                    command.Add(single[0]);
                    if (single[0] == 0) break;
                }

                var payload = new List<byte>();
                while (true)
                {
                    var lengthBytes = await ReadExactlyAsync(stream, 4);
                    if (lengthBytes is null) break;
                    var length = (int)BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
                    if (length == 0) break;
                    var chunk = await ReadExactlyAsync(stream, length);
                    if (chunk is null) break;
                    payload.AddRange(chunk);
                }

                received.TrySetResult((Encoding.ASCII.GetString(command.ToArray()), payload.ToArray()));

                var response = Encoding.ASCII.GetBytes(reply + "\0");
                await stream.WriteAsync(response, cancellation.Token);
                await stream.FlushAsync(cancellation.Token);
            }
            catch (Exception exception)
            {
                received.TrySetException(exception);
            }
        }

        private async Task<byte[]?> ReadExactlyAsync(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellation.Token);
                if (read <= 0) return offset == 0 ? null : buffer;
                offset += read;
            }

            return buffer;
        }
    }
}
