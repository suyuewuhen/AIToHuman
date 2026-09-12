using AIToHuman.Application.Orders;
using AIToHuman.Application.Settings;
using AIToHuman.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace AIToHuman.Infrastructure.Storage;

/// <summary>
/// 按运营配置 <c>evidence.scanner.provider</c> 选择扫描实现：
/// <c>none</c> 显式放行（打警告日志，绝不假装扫过）、<c>http</c> 调外部扫描服务、<c>clamav</c> 走 clamd 的 INSTREAM 协议。
/// 每次调用都重新读取配置，因此在运营后台切换扫描方式不需要重启进程。
/// </summary>
public sealed class SettingsEvidenceScanner(
    ISettingsProvider settings,
    HttpEvidenceScanner http,
    ClamAvEvidenceScanner clamAv,
    ILogger<SettingsEvidenceScanner> logger) : IEvidenceScanner
{
    public async Task<EvidenceScanStatus> ScanAsync(string storageKey, string contentType, CancellationToken cancellationToken = default)
    {
        var provider = settings.GetChoice(SettingKeys.EvidenceScannerProvider, "none");
        return provider switch
        {
            "http" => await http.ScanAsync(storageKey, contentType, cancellationToken),
            "clamav" => await clamAv.ScanAsync(storageKey, contentType, cancellationToken),
            _ => AllowWithoutScanning(provider, storageKey)
        };
    }

    private EvidenceScanStatus AllowWithoutScanning(string provider, string storageKey)
    {
        logger.LogWarning(
            "尚未接入凭证安全检查（provider={Provider}），已直接放行 {StorageKey}。生产环境请在运营后台切换为 http 或 clamav。",
            provider,
            storageKey);
        return EvidenceScanStatus.Clean;
    }
}
