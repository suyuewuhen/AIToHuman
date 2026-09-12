using System.Net.Sockets;
using System.Text;
using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace AIToHuman.Infrastructure.Storage;

/// <summary>
/// ClamAV 的 <c>clamd</c> 扫描器（INSTREAM 协议）。
/// 病毒库与引擎由部署方自己维护：只需要在运营配置里填 clamd 的地址与端口即可，
/// 不需要引入任何厂商 SDK，也不把凭证文件写进临时目录——内容是分块推给 clamd 的。
///
/// 协议：连接后发 <c>zINSTREAM\0</c>，随后是若干「4 字节大端长度 + 数据」块，
/// 最后发一个长度为 0 的块表示结束；clamd 回一行以 NUL 结尾的结果：
/// <c>stream: OK</c>、<c>stream: Eicar-Test-Signature FOUND</c> 或 <c>... ERROR</c>。
/// </summary>
public sealed class ClamAvEvidenceScanner(
    ISettingsProvider settings,
    IFileStorage storage,
    ILogger<ClamAvEvidenceScanner> logger) : IEvidenceScanner
{
    /// <summary><c>z</c> 前缀表示命令以 NUL 结尾。</summary>
    private static readonly byte[] InstreamCommand = Encoding.ASCII.GetBytes("zINSTREAM\0");

    private const int ChunkSize = 64 * 1024;
    private const int MaxReplyBytes = 1024;

    public async Task<EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        var host = settings.GetValue(SettingKeys.EvidenceScannerClamAvHost) ?? ClamAvDefaults.Host;
        var port = settings.GetInt(SettingKeys.EvidenceScannerClamAvPort) ?? ClamAvDefaults.Port;
        var timeoutSeconds = settings.GetInt(SettingKeys.EvidenceScannerTimeoutSeconds) ?? ClamAvDefaults.TimeoutSeconds;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, timeout.Token);
            await using var stream = client.GetStream();

            await stream.WriteAsync(InstreamCommand, timeout.Token);

            var content = await storage.OpenReadAsync(storageKey, timeout.Token)
                ?? throw new InvalidOperationException($"凭证文件 {storageKey} 不存在，无法扫描。");
            await using (content)
            {
                var buffer = new byte[ChunkSize];
                while (true)
                {
                    var read = await content.ReadAsync(buffer, timeout.Token);
                    if (read <= 0) break;
                    await WriteChunkAsync(stream, buffer.AsMemory(0, read), timeout.Token);
                }
            }

            // 长度为 0 的块 = 传输结束。
            await stream.WriteAsync(new byte[4], timeout.Token);
            await stream.FlushAsync(timeout.Token);

            var reply = await ReadReplyAsync(stream, timeout.Token);
            return Interpret(reply, storageKey);
        }
        catch (Exception exception) when (exception is SocketException or IOException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogError(exception, "调用 clamd（{Host}:{Port}）扫描 {StorageKey} 失败，按失败模式处理。", host, port, storageKey);
            return Unavailable();
        }
    }

    private static async Task WriteChunkAsync(NetworkStream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    /// <summary>读到 NUL 或换行为止；clamd 对 z 前缀命令用 NUL 结尾。</summary>
    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxReplyBytes];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), cancellationToken);
            if (read <= 0) break;
            var start = count;
            count += read;
            for (var index = start; index < count; index++)
            {
                if (buffer[index] is 0 or (byte)'\n')
                {
                    return Encoding.ASCII.GetString(buffer, 0, index).TrimEnd('\r');
                }
            }
        }

        return Encoding.ASCII.GetString(buffer, 0, count).TrimEnd('\r', '\n', '\0');
    }

    private EvidenceScanStatus Interpret(string reply, string storageKey)
    {
        var normalized = reply.Trim();
        if (normalized.EndsWith("OK", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("clamd 判定 {StorageKey} 干净。", storageKey);
            return EvidenceScanStatus.Clean;
        }

        if (normalized.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
        {
            // 形如 "stream: Eicar-Test-Signature FOUND"，把命中名称记进日志便于排查。
            logger.LogWarning("clamd 在 {StorageKey} 中检出威胁：{Reply}", storageKey, normalized);
            return EvidenceScanStatus.Rejected;
        }

        logger.LogError("clamd 返回了无法识别的结果（{Reply}），{StorageKey} 按失败模式处理。", normalized, storageKey);
        return Unavailable();
    }

    /// <summary>扫描不可用时的兜底：closed 保持 Pending（保留文件但不可下载），open 放行。</summary>
    private EvidenceScanStatus Unavailable() =>
        settings.GetChoice(SettingKeys.EvidenceScannerFailMode, "closed") == "open"
            ? EvidenceScanStatus.Clean
            : EvidenceScanStatus.Pending;
}

/// <summary>clamd 的默认连接参数。</summary>
public static class ClamAvDefaults
{
    public const string Host = "127.0.0.1";
    public const int Port = 3310;
    public const int TimeoutSeconds = 15;
}
