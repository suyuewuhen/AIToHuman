using System.Buffers.Binary;
using System.Text;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 元数据剥离：只丢 EXIF/GPS/文本这类元数据段，像素数据必须逐字节保留。
/// 这里用手工拼出来的最小合法结构验证，不依赖任何图像库。
/// </summary>
public sealed class EvidenceContentSanitizerTests
{
    [Fact]
    public void Jpeg_loses_exif_and_comments_but_keeps_everything_else()
    {
        var exif = Segment(0xE1, "Exif\0\0GPS: 39.90,116.40"u8.ToArray());
        var comment = Segment(0xFE, "shot on my phone"u8.ToArray());
        var jfif = Segment(0xE0, "JFIF\0"u8.ToArray());
        var frame = Segment(0xC0, [0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]);
        var scan = Segment(0xDA, [0x01, 0x01, 0x00]);
        byte[] entropy = [0x12, 0x34, 0x56, 0xFF, 0x00, 0x78];
        var input = Concat([0xFF, 0xD8], exif, jfif, comment, frame, scan, entropy, [0xFF, 0xD9]);

        var result = EvidenceContentSanitizer.StripMetadata("image/jpeg", input);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "EXIF/XMP", "JPEG 注释" }, result.Removed);
        // 保留下来的段与熵编码数据必须逐字节一致。
        var expected = Concat([0xFF, 0xD8], jfif, frame, scan, entropy, [0xFF, 0xD9]);
        Assert.Equal(expected, result.Bytes);
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(result.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_without_metadata_is_returned_untouched()
    {
        var jfif = Segment(0xE0, "JFIF\0"u8.ToArray());
        var scan = Segment(0xDA, [0x01, 0x01, 0x00]);
        var input = Concat([0xFF, 0xD8], jfif, scan, [0xAA, 0xBB], [0xFF, 0xD9]);

        var result = EvidenceContentSanitizer.StripMetadata("image/jpeg", input);

        Assert.False(result.Changed);
        Assert.Empty(result.Removed);
        Assert.Same(input, result.Bytes);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x40, 0x01 })]
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x11 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x02 })]
    public void Broken_jpeg_is_never_rewritten(byte[] input)
    {
        var result = EvidenceContentSanitizer.StripMetadata("image/jpeg", input);

        Assert.False(result.Changed);
        Assert.Same(input, result.Bytes);
    }

    [Fact]
    public void Png_loses_text_time_and_exif_chunks()
    {
        var text = PngChunk("tEXt", "Comment\0GPS 39.90,116.40"u8.ToArray());
        var time = PngChunk("tIME", [0x07, 0xEA, 0x09, 0x0C, 0x08, 0x00, 0x00]);
        var exif = PngChunk("eXIf", "Exif\0\0"u8.ToArray());
        var header = PngChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        var pixels = PngChunk("IDAT", [0x78, 0x9C, 0x63, 0x00, 0x01]);
        var end = PngChunk("IEND", []);
        var input = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, text, time, exif, pixels, end);

        var result = EvidenceContentSanitizer.StripMetadata("image/png", input);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "PNG tEXt", "PNG tIME", "PNG eXIf" }, result.Removed);
        var expected = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, pixels, end);
        Assert.Equal(expected, result.Bytes);
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(result.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Png_without_metadata_is_returned_untouched()
    {
        var header = PngChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        var pixels = PngChunk("IDAT", [0x78, 0x9C, 0x63]);
        var input = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, pixels, PngChunk("IEND", []));

        var result = EvidenceContentSanitizer.StripMetadata("image/png", input);

        Assert.False(result.Changed);
        Assert.Same(input, result.Bytes);
    }

    [Fact]
    public void Webp_loses_exif_and_xmp_and_fixes_the_header_flags_and_length()
    {
        // VP8X 的标志位：0x0C 表示“含 EXIF 与 XMP”。丢掉块之后这两位必须清掉。
        var vp8x = WebpChunk("VP8X", [0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var exif = WebpChunk("EXIF", "Exif\0\0GPS 39.90"u8.ToArray());
        var xmp = WebpChunk("XMP ", "xmpmeta"u8.ToArray());
        var pixels = WebpChunk("VP8L", [0x2F, 0x00, 0x00, 0x00, 0x00]);
        var body = Concat(vp8x, exif, xmp, pixels);
        var input = Riff(body);

        var result = EvidenceContentSanitizer.StripMetadata("image/webp", input);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "WebP EXIF", "WebP XMP" }, result.Removed);
        var expected = Riff(Concat(WebpChunk("VP8X", [0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0]), pixels));
        Assert.Equal(expected, result.Bytes);
        // RIFF 的总长度字段必须跟着改，否则解码器会认为文件被截断。
        Assert.Equal((uint)(result.Bytes.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(result.Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void Webp_without_metadata_is_returned_untouched()
    {
        var input = Riff(Concat(WebpChunk("VP8L", [0x2F, 0x00, 0x00, 0x00, 0x00])));

        var result = EvidenceContentSanitizer.StripMetadata("image/webp", input);

        Assert.False(result.Changed);
        Assert.Same(input, result.Bytes);
    }

    [Fact]
    public void Pdf_and_unknown_types_are_not_rewritten()
    {
        byte[] pdf = "%PDF-1.4 /Author (someone)"u8.ToArray();

        var result = EvidenceContentSanitizer.StripMetadata("application/pdf", pdf);

        Assert.False(result.Changed);
        Assert.Same(pdf, result.Bytes);
        Assert.Empty(result.Removed);
    }

    /// <summary>JPEG 段：FF &lt;marker&gt; &lt;2 字节大端长度（含自身）&gt; 载荷。</summary>
    private static byte[] Segment(byte marker, byte[] payload)
    {
        var length = payload.Length + 2;
        return Concat([0xFF, marker, (byte)(length >> 8), (byte)(length & 0xFF)], payload);
    }

    /// <summary>PNG 块：4 字节大端长度 + 4 字节类型 + 载荷 + 4 字节 CRC（这里用 0 占位，剥离逻辑不校验 CRC）。</summary>
    private static byte[] PngChunk(string type, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        return Concat(length, Encoding.ASCII.GetBytes(type), payload, [0, 0, 0, 0]);
    }

    /// <summary>WebP 子块：4 字节 fourcc + 4 字节小端长度 + 载荷（奇数长度补 1 字节）。</summary>
    private static byte[] WebpChunk(string fourCc, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        var chunk = Concat(Encoding.ASCII.GetBytes(fourCc), length, payload);
        return payload.Length % 2 == 1 ? Concat(chunk, [0]) : chunk;
    }

    private static byte[] Length(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value);
        return bytes;
    }

    /// <summary>按 RIFF 规范拼 WebP：长度字段 = 文件长度 - 8（含 "WEBP" 四个字节）。</summary>
    private static byte[] Riff(byte[] body) =>
        Concat("RIFF"u8.ToArray(), Length(body.Length + 4), "WEBP"u8.ToArray(), body);

    private static byte[] Concat(params byte[][] parts)
    {
        var output = new List<byte>();
        foreach (var part in parts) output.AddRange(part);
        return output.ToArray();
    }
}
