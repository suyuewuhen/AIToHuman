using System.Buffers.Binary;
using AIToHuman.Domain.Common;

namespace AIToHuman.Domain.Orders;

/// <summary>去除元数据后的内容，以及被丢掉了哪些元数据（用于留痕与展示）。</summary>
public sealed record SanitizedContent(byte[] Bytes, IReadOnlyList<string> Removed)
{
    public bool Changed => Removed.Count > 0;
}

/// <summary>
/// 上传内容的元数据剥离与容器规范化：JPEG 丢掉**全部** APPn 扩展段与注释，PNG 只保留
/// 结构必需的块（IHDR/PLTE/tRNS/IDAT/IEND），WebP 只保留图像数据块。
/// **不改动像素数据**，也不需要图像库（纯字节解析，离线可用）。
///
/// 为什么默认要做：EXIF 里常带拍摄地点（GPS）、设备序列号与拍摄时间，
/// 服务者上传取件凭证时很可能无意中暴露住址。保留原始文件则把它当成合规审计资料，
/// 因此这是运营可配置项（<c>evidence.stripMetadata</c>），默认开启。
///
/// 两条口径：
/// 一是**白名单而不是黑名单**：任何不认识的扩展段/块都丢掉，而不是只挑已知的元数据段——
/// 嵌入载荷最省事的藏法就是"造一个私有块"，黑名单永远追不上；丢掉之后文件仍然是合法的
/// JPEG/PNG/WebP（APPn 与辅助块都是可选的）。
/// 二是**结构坏了就拒绝**（fail closed）：声明了 image/jpeg 却缺 SOS/EOI、PNG 缺 IEND、
/// WebP 块长度越界这类情况不再是"原样放行"，而是抛错让上传失败——
/// 存一份自己都解析不了的文件，等于把它交给下游解码器与扫描器去赌运气。
///
/// 说明：这里做的是**容器规范化**，不是像素级重编码（重编码需要图像编解码库，
/// 当前环境离线取不到，见 docs/security/security-and-risk.md）。真正兜底的是内容扫描。
/// PDF 不在这里处理：它没有统一的块结构，剥离需要解析 PDF 语法，留给后续按需实现。
/// </summary>
public static class EvidenceContentSanitizer
{
    /// <summary>PNG 里允许保留的块类型：全是结构必需的块，其余（含未知的私有块）一律丢弃。</summary>
    private static readonly HashSet<string> PngStructuralChunks = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "tRNS", "IDAT", "IEND"
    };

    /// <summary>WebP 里允许保留的块：图像数据与动画容器，其余（EXIF/XMP 与未知块）一律丢弃。</summary>
    private static readonly HashSet<string> WebpStructuralChunks = new(StringComparer.Ordinal)
    {
        "VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF"
    };

    public static SanitizedContent StripMetadata(string? contentType, byte[] content)
    {
        var normalized = OrderEvidence.NormalizeContentType(contentType);
        return normalized switch
        {
            "image/jpeg" => StripJpeg(content),
            "image/png" => StripPng(content),
            "image/webp" => StripWebp(content),
            _ => new SanitizedContent(content, [])
        };
    }

    /// <summary>结构不符合已声明的图片格式：拒绝保存，而不是原样放行。</summary>
    private static DomainException Malformed(string contentType, string detail) =>
        new($"凭证内容不是合法的 {contentType}（{detail}），已拒绝保存。");

    private static SanitizedContent StripJpeg(byte[] content)
    {
        // 段结构：FFD8(SOI) 之后是一串 FF <marker> <2 字节长度> <载荷>；遇到 FFDA(SOS) 之后是压缩数据直到 FFD9(EOI)。
        if (content.Length < 4 || content[0] != 0xFF || content[1] != 0xD8) throw Malformed("JPEG", "缺少文件头");

        var output = new List<byte>(content.Length);
        output.AddRange(content.AsSpan(0, 2).ToArray());
        var removed = new List<string>();
        var index = 2;
        var sawEnd = false;

        while (index < content.Length)
        {
            if (content[index] != 0xFF) throw Malformed("JPEG", "段结构不完整");

            var markerStart = index;
            // 跳过 0xFF 填充字节，取真正的标记字节。
            while (index + 1 < content.Length && content[index + 1] == 0xFF) index++;
            if (index + 1 >= content.Length) throw Malformed("JPEG", "标记被截断");

            var marker = content[index + 1];
            index += 2;

            if (marker == 0xD9)
            {
                // EOI：整段照抄（含可能存在的填充字节）后结束。
                output.AddRange(content.AsSpan(markerStart, index - markerStart).ToArray());
                sawEnd = true;
                break;
            }

            // 无载荷的标记：TEM(01) 与 RSTn(D0-D7)。
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.AddRange(content.AsSpan(markerStart, index - markerStart).ToArray());
                continue;
            }

            if (index + 2 > content.Length) throw Malformed("JPEG", "段长度被截断");
            var length = (content[index] << 8) | content[index + 1];
            if (length < 2 || index + length > content.Length) throw Malformed("JPEG", "段长度越界");

            var segmentEnd = index + length;
            // APPn（0xE0–0xEF，含 JFIF 与 ICC）与 COM（注释）都是可选扩展段，一律丢掉：
            // 它们是元数据与嵌入载荷最常见的藏身处，丢掉之后文件仍然合法。
            if (IsJpegExtensionSegment(marker))
            {
                removed.Add(DescribeJpegSegment(marker));
            }
            else
            {
                output.AddRange(content.AsSpan(markerStart, index - markerStart).ToArray());
                output.AddRange(content.AsSpan(index, length).ToArray());
            }

            index = segmentEnd;

            if (marker == 0xDA)
            {
                // SOS 之后是熵编码数据，整段照抄到文件结尾。
                output.AddRange(content.AsSpan(index).ToArray());
                break;
            }
        }

        if (!sawEnd)
        {
            // SOS 之后的熵编码数据里必须能找到结束标记；找不到说明文件被截断（解码器与扫描器都只能靠猜）。
            var tail = content.AsSpan(Math.Min(index, content.Length));
            if (tail.IndexOf(JpegEndMarker) < 0) throw Malformed("JPEG", "缺少结束标记");
        }

        return removed.Count == 0 ? new SanitizedContent(content, []) : new SanitizedContent(output.ToArray(), removed);
    }

    private static ReadOnlySpan<byte> JpegEndMarker => [0xFF, 0xD9];

    private static bool IsJpegExtensionSegment(byte marker) => (marker >= 0xE0 && marker <= 0xEF) || marker == 0xFE;

    /// <summary>丢掉的是什么：常用的两类给中文说明，其余按 APPn 编号写清楚。</summary>
    private static string DescribeJpegSegment(byte marker) => marker switch
    {
        0xE1 => "EXIF/XMP",
        0xFE => "JPEG 注释",
        0xE0 => "JPEG APP0(JFIF)",
        _ => $"JPEG APP{marker - 0xE0}"
    };

    private static SanitizedContent StripPng(byte[] content)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (content.Length < 8 || !content.AsSpan(0, 8).SequenceEqual(signature)) throw Malformed("PNG", "缺少文件头");

        var output = new List<byte>(content.Length);
        output.AddRange(content.AsSpan(0, 8).ToArray());
        var removed = new List<string>();
        var index = 8;
        var sawEnd = false;
        var dataChunks = 0;

        while (index + 12 <= content.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(index, 4));
            if (length < 0 || index + 12 + length > content.Length) throw Malformed("PNG", "块长度越界");

            var type = System.Text.Encoding.ASCII.GetString(content, index + 4, 4);
            var total = 12 + length;
            if (PngStructuralChunks.Contains(type))
            {
                if (type == "IDAT") dataChunks++;
                output.AddRange(content.AsSpan(index, total).ToArray());
            }
            else
            {
                removed.Add(DescribePngChunk(type));
            }

            index += total;
            if (type == "IEND")
            {
                sawEnd = true;
                break;
            }
        }

        if (!sawEnd) throw Malformed("PNG", "缺少结束块 IEND");
        if (dataChunks == 0) throw Malformed("PNG", "没有图像数据块 IDAT");

        return removed.Count == 0 ? new SanitizedContent(content, []) : new SanitizedContent(output.ToArray(), removed);
    }

    /// <summary>已知的元数据块用原来的名字（界面与留痕都在用），其余一律标成"未知块"。</summary>
    private static string DescribePngChunk(string type) => type switch
    {
        "tEXt" or "zTXt" or "iTXt" or "eXIf" or "tIME" => $"PNG {type}",
        _ => $"PNG 未知块({type})"
    };

    private static SanitizedContent StripWebp(byte[] content)
    {
        // RIFF 结构：'RIFF' <4 字节总长> 'WEBP' 之后是一串 <4 字节 fourcc> <4 字节小端长度> <载荷>（奇数长度补 1 字节）。
        if (content.Length < 12
            || !content.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !content.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            throw Malformed("WebP", "缺少文件头");

        var chunks = new List<(string FourCc, byte[] Payload)>();
        var removed = new List<string>();
        var index = 12;

        while (index + 8 <= content.Length)
        {
            var fourCc = System.Text.Encoding.ASCII.GetString(content, index, 4);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(index + 4, 4));
            if (length < 0 || index + 8 + length > content.Length) throw Malformed("WebP", "块长度越界");

            var payload = content.AsSpan(index + 8, length).ToArray();
            if (WebpStructuralChunks.Contains(fourCc))
            {
                chunks.Add((fourCc, payload));
            }
            else
            {
                removed.Add(fourCc switch { "EXIF" => "WebP EXIF", "XMP " => "WebP XMP", _ => $"WebP 未知块({fourCc.Trim()})" });
            }

            index += 8 + length + (length % 2 == 1 ? 1 : 0);
        }

        if (chunks.Count == 0) throw Malformed("WebP", "没有图像数据块");

        if (removed.Count == 0) return new SanitizedContent(content, []);

        // VP8X 的头 1 字节是扩展标志位：EXIF 是 0x08、XMP 是 0x04。丢掉块却不改标志位，
        // 有些解码器会认为文件损坏，所以这里把对应位清掉。
        for (var i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].FourCc != "VP8X" || chunks[i].Payload.Length == 0) continue;
            var payload = chunks[i].Payload;
            payload[0] = (byte)(payload[0] & ~0x0C);
            chunks[i] = ("VP8X", payload);
        }

        var output = new List<byte>();
        output.AddRange("RIFF"u8.ToArray());
        output.AddRange(new byte[4]);          // 总长度稍后回填
        output.AddRange("WEBP"u8.ToArray());
        foreach (var (fourCc, payload) in chunks)
        {
            output.AddRange(System.Text.Encoding.ASCII.GetBytes(fourCc));
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, (uint)payload.Length);
            output.AddRange(lengthBytes);
            output.AddRange(payload);
            if (payload.Length % 2 == 1) output.Add(0);
        }

        var bytes = output.ToArray();
        // RIFF 头的总长度字段 = 文件长度 - 8，丢块之后必须回填，否则解码器会认为文件被截断。
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)(bytes.Length - 8));
        return new SanitizedContent(bytes, removed);
    }
}
