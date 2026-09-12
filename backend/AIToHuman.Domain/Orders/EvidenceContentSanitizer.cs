using System.Buffers.Binary;

namespace AIToHuman.Domain.Orders;

/// <summary>去除元数据后的内容，以及被丢掉了哪些元数据（用于留痕与展示）。</summary>
public sealed record SanitizedContent(byte[] Bytes, IReadOnlyList<string> Removed)
{
    public bool Changed => Removed.Count > 0;
}

/// <summary>
/// 上传内容的元数据剥离：JPEG 的 EXIF/XMP 与注释、PNG 的文本/时间/EXIF 块、WebP 的 EXIF/XMP 块。
/// 只丢元数据段，**不改动像素数据**，也不需要图像库（纯字节解析，离线可用）。
///
/// 为什么默认要做：EXIF 里常带拍摄地点（GPS）、设备序列号与拍摄时间，
/// 服务者上传取件凭证时很可能无意中暴露住址。保留原始文件则把它当成合规审计资料，
/// 因此这是运营可配置项（<c>evidence.stripMetadata</c>），默认开启。
///
/// 解析失败（文件损坏或不符合已声明的格式）时原样返回，不冒险改写内容。
/// </summary>
public static class EvidenceContentSanitizer
{
    /// <summary>PNG 里属于元数据、需要丢弃的块类型。</summary>
    private static readonly HashSet<string> PngMetadataChunks = new(StringComparer.Ordinal)
    {
        "tEXt", "zTXt", "iTXt", "eXIf", "tIME"
    };

    /// <summary>WebP 里属于元数据、需要丢弃的 RIFF 子块。</summary>
    private static readonly HashSet<string> WebpMetadataChunks = new(StringComparer.Ordinal)
    {
        "EXIF", "XMP "
    };

    public static SanitizedContent StripMetadata(string? contentType, byte[] content)
    {
        var normalized = OrderEvidence.NormalizeContentType(contentType);
        return normalized switch
        {
            "image/jpeg" => StripJpeg(content),
            "image/png" => StripPng(content),
            "image/webp" => StripWebp(content),
            // PDF 不在这里处理：它没有统一的元数据块结构，剥离需要解析 PDF 语法，留给后续按需实现。
            _ => new SanitizedContent(content, [])
        };
    }

    private static SanitizedContent StripJpeg(byte[] content)
    {
        // 段结构：FFD8(SOI) 之后是一串 FF <marker> <2 字节长度> <载荷>；遇到 FFDA(SOS) 之后是压缩数据直到 FFD9(EOI)。
        if (content.Length < 4 || content[0] != 0xFF || content[1] != 0xD8) return new SanitizedContent(content, []);

        var output = new List<byte>(content.Length);
        output.AddRange(content.AsSpan(0, 2).ToArray());
        var removed = new List<string>();
        var index = 2;

        while (index < content.Length)
        {
            if (content[index] != 0xFF) return new SanitizedContent(content, []);

            var markerStart = index;
            // 跳过 0xFF 填充字节，取真正的标记字节。
            while (index + 1 < content.Length && content[index + 1] == 0xFF) index++;
            if (index + 1 >= content.Length) return new SanitizedContent(content, []);

            var marker = content[index + 1];
            index += 2;

            if (marker == 0xD9)
            {
                // EOI：整段照抄（含可能存在的填充字节）后结束。
                output.AddRange(content.AsSpan(markerStart, index - markerStart).ToArray());
                break;
            }

            // 无载荷的标记：TEM(01) 与 RSTn(D0-D7)。
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                output.AddRange(content.AsSpan(markerStart, index - markerStart).ToArray());
                continue;
            }

            if (index + 2 > content.Length) return new SanitizedContent(content, []);
            var length = (content[index] << 8) | content[index + 1];
            if (length < 2 || index + length > content.Length) return new SanitizedContent(content, []);

            var segmentEnd = index + length;
            // APP1(EXIF/XMP) 与 COM(注释) 属于元数据，丢掉；其余段（含 JFIF、ICC 色彩配置）原样保留。
            var isMetadata = marker == 0xE1 || marker == 0xFE;
            if (isMetadata)
            {
                removed.Add(marker == 0xE1 ? "EXIF/XMP" : "JPEG 注释");
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

        return removed.Count == 0 ? new SanitizedContent(content, []) : new SanitizedContent(output.ToArray(), removed);
    }

    private static SanitizedContent StripPng(byte[] content)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (content.Length < 8 || !content.AsSpan(0, 8).SequenceEqual(signature)) return new SanitizedContent(content, []);

        var output = new List<byte>(content.Length);
        output.AddRange(content.AsSpan(0, 8).ToArray());
        var removed = new List<string>();
        var index = 8;

        while (index + 12 <= content.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(index, 4));
            if (length < 0 || index + 12 + length > content.Length) return new SanitizedContent(content, []);

            var type = System.Text.Encoding.ASCII.GetString(content, index + 4, 4);
            var total = 12 + length;
            if (PngMetadataChunks.Contains(type))
            {
                removed.Add($"PNG {type}");
            }
            else
            {
                output.AddRange(content.AsSpan(index, total).ToArray());
            }

            index += total;
            if (type == "IEND") break;
        }

        return removed.Count == 0 ? new SanitizedContent(content, []) : new SanitizedContent(output.ToArray(), removed);
    }

    private static SanitizedContent StripWebp(byte[] content)
    {
        // RIFF 结构：'RIFF' <4 字节总长> 'WEBP' 之后是一串 <4 字节 fourcc> <4 字节小端长度> <载荷>（奇数长度补 1 字节）。
        if (content.Length < 12
            || !content.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !content.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return new SanitizedContent(content, []);

        var chunks = new List<(string FourCc, byte[] Payload)>();
        var removed = new List<string>();
        var index = 12;

        while (index + 8 <= content.Length)
        {
            var fourCc = System.Text.Encoding.ASCII.GetString(content, index, 4);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(index + 4, 4));
            if (length < 0 || index + 8 + length > content.Length) return new SanitizedContent(content, []);

            var payload = content.AsSpan(index + 8, length).ToArray();
            if (WebpMetadataChunks.Contains(fourCc))
            {
                removed.Add($"WebP {fourCc.Trim()}");
            }
            else
            {
                chunks.Add((fourCc, payload));
            }

            index += 8 + length + (length % 2 == 1 ? 1 : 0);
        }

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
