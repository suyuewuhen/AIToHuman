using System.Buffers.Binary;
using System.Text;
using AIToHuman.Domain.Common;
using AIToHuman.Domain.Orders;

namespace AIToHuman.Domain.Tests;

/// <summary>
/// 元数据剥离：只丢 EXIF/GPS/文本这类元数据段，像素数据必须逐字节保留。
/// 这里用手工拼出来的最小合法结构验证，不依赖任何图像库。
/// </summary>
public sealed class EvidenceContentSanitizerTests
{
    [Fact]
    public void Jpeg_loses_every_extension_segment_and_comment_but_keeps_the_structure()
    {
        var exif = Segment(0xE1, "Exif\0\0GPS: 39.90,116.40"u8.ToArray());
        var jfif = Segment(0xE0, "JFIF\0"u8.ToArray());
        // APP13（Photoshop IRB）过去是保留的：白名单口径下所有 APPn 都要丢掉。
        var photoshop = Segment(0xED, "Photoshop 3.0\0"u8.ToArray());
        var comment = Segment(0xFE, "shot on my phone"u8.ToArray());
        var frame = Segment(0xC0, [0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]);
        var scan = Segment(0xDA, [0x01, 0x01, 0x00]);
        byte[] entropy = [0x12, 0x34, 0x56, 0xFF, 0x00, 0x78];
        var input = Concat([0xFF, 0xD8], exif, jfif, photoshop, comment, frame, scan, entropy, [0xFF, 0xD9]);

        var result = EvidenceContentSanitizer.StripMetadata("image/jpeg", input);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "EXIF/XMP", "JPEG APP0(JFIF)", "JPEG APP13", "JPEG 注释" }, result.Removed);
        // 保留下来的结构段与熵编码数据必须逐字节一致（像素数据一个字节都不许动）。
        var expected = Concat([0xFF, 0xD8], frame, scan, entropy, [0xFF, 0xD9]);
        Assert.Equal(expected, result.Bytes);
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(result.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Jpeg_without_extension_segments_is_returned_untouched()
    {
        var frame = Segment(0xC0, [0x08, 0x00, 0x10, 0x00, 0x10, 0x01, 0x01, 0x11, 0x00]);
        var scan = Segment(0xDA, [0x01, 0x01, 0x00]);
        var input = Concat([0xFF, 0xD8], frame, scan, [0xAA, 0xBB], [0xFF, 0xD9]);

        var result = EvidenceContentSanitizer.StripMetadata("image/jpeg", input);

        Assert.False(result.Changed);
        Assert.Empty(result.Removed);
        Assert.Same(input, result.Bytes);
    }

    [Theory]
    // 段长度越界（声称 0x40 字节，实际只剩 1 字节）。
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x40, 0x01 })]
    // 期望一个标记字节，拿到的却是 0x00。
    [InlineData(new byte[] { 0xFF, 0xD8, 0x00, 0x11 })]
    // 声明成 JPEG 却根本没有文件头。
    [InlineData(new byte[] { 0x00, 0x01, 0x02 })]
    // 有 SOS 但没有结束标记（文件被截断）。
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0x00, 0x03, 0x01, 0x01, 0x00, 0x12, 0x34 })]
    public void Malformed_jpeg_is_rejected_instead_of_stored_as_is(byte[] input)
    {
        // fail closed：解析不了的"图片"不再原样放行，而是让上传失败。
        var error = Assert.Throws<DomainException>(() => EvidenceContentSanitizer.StripMetadata("image/jpeg", input));

        Assert.Contains("已拒绝保存", error.Message);
    }

    [Fact]
    public void Png_loses_metadata_and_unknown_private_chunks()
    {
        var text = PngChunk("tEXt", "Comment\0GPS 39.90,116.40"u8.ToArray());
        var time = PngChunk("tIME", [0x07, 0xEA, 0x09, 0x0C, 0x08, 0x00, 0x00]);
        var exif = PngChunk("eXIf", "Exif\0\0"u8.ToArray());
        // 私有块是嵌入载荷最省事的藏身处：白名单口径下不管认不认识都丢掉。
        var payload = PngChunk("prVt", "PK\u0003\u0004 假装一个 zip"u8.ToArray());
        var header = PngChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        var pixels = PngChunk("IDAT", [0x78, 0x9C, 0x63, 0x00, 0x01]);
        var end = PngChunk("IEND", []);
        var input = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, text, time, exif, payload, pixels, end);

        var result = EvidenceContentSanitizer.StripMetadata("image/png", input);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "PNG tEXt", "PNG tIME", "PNG eXIf", "PNG 未知块(prVt)" }, result.Removed);
        var expected = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, pixels, end);
        Assert.Equal(expected, result.Bytes);
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(result.Bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("PK", Encoding.Latin1.GetString(result.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Png_without_extra_chunks_is_returned_untouched()
    {
        var header = PngChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        var pixels = PngChunk("IDAT", [0x78, 0x9C, 0x63]);
        var input = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, pixels, PngChunk("IEND", []));

        var result = EvidenceContentSanitizer.StripMetadata("image/png", input);

        Assert.False(result.Changed);
        Assert.Same(input, result.Bytes);
    }

    [Theory]
    // 缺 IEND（文件被截断）。
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    public void Malformed_png_is_rejected_instead_of_stored_as_is(byte[] header)
    {
        var error = Assert.Throws<DomainException>(() => EvidenceContentSanitizer.StripMetadata("image/png", header));

        Assert.Contains("已拒绝保存", error.Message);
    }

    [Fact]
    public void Png_without_pixel_data_is_rejected()
    {
        var header = PngChunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
        var input = Concat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], header, PngChunk("IEND", []));

        var error = Assert.Throws<DomainException>(() => EvidenceContentSanitizer.StripMetadata("image/png", input));

        Assert.Contains("IDAT", error.Message);
    }

    [Fact]
    public void Webp_loses_exif_and_xmp_and_fixes_the_header_flags_and_length()
    {
        // VP8X 的标志位：0x0C 表示“含 EXIF 与 XMP”。丢掉块之后这两位必须清掉。
        var vp8x = WebpChunk("VP8X", [0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var exif = WebpChunk("EXIF", "Exif\0\0GPS 39.90"u8.ToArray());
        var xmp = WebpChunk("XMP ", "xmpmeta"u8.ToArray());
        // 未知块同样丢掉（白名单口径）。
        var privateChunk = WebpChunk("prVt", "PK\u0003\u0004"u8.ToArray());
        var pixels = WebpChunk("VP8L", [0x2F, 0x00, 0x00, 0x00, 0x00]);
        var body = Concat(vp8x, exif, xmp, privateChunk, pixels);
        var input = Riff(body);

        var result = EvidenceContentSanitizer.StripMetadata("image/webp", input);

        Assert.True(result.Changed);
        Assert.Equal(new[] { "WebP EXIF", "WebP XMP", "WebP 未知块(prVt)" }, result.Removed);
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
    public void Malformed_webp_is_rejected_instead_of_stored_as_is()
    {
        // RIFF 头声明了 4096 字节的子块，实际只有 5 字节：块长度越界。
        var broken = Concat("RIFF"u8.ToArray(), Length(4 + 8 + 4096), "WEBP"u8.ToArray(), Encoding.ASCII.GetBytes("VP8L"), Length(4096), [0x2F, 0, 0, 0, 0]);

        var error = Assert.Throws<DomainException>(() => EvidenceContentSanitizer.StripMetadata("image/webp", broken));

        Assert.Contains("已拒绝保存", error.Message);
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
