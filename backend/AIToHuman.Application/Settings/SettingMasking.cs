using System.Security.Cryptography;
using System.Text;

namespace AIToHuman.Application.Settings;

/// <summary>
/// 机密值的展示与留痕规则：接口响应和审计记录里只出现掩码与指纹，永远不出现明文。
/// </summary>
public static class SettingMasking
{
    /// <summary>取值为空时展示的文案。</summary>
    public const string EmptyDisplay = "(空)";

    /// <summary>保留末四位便于运营核对“是不是这把钥匙”，其余用星号遮盖。</summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return EmptyDisplay;
        return value.Length <= 4 ? "****" : $"****{value[^4..]}";
    }

    /// <summary>取值指纹：能确认“换过没有”，但无法反推明文。</summary>
    public static string Fingerprint(string? value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}
