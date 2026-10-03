using System.Buffers.Binary;
using System.IO.Compression;

namespace NegiCraftLauncher.Raster;

/// <summary>一张解码出来的图：直通 alpha、RGBA 字节序、行优先。</summary>
public sealed class RawImage
{
    public RawImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Rgba { get; }

    public PixelBuffer ToPixelBuffer() => PixelBuffer.FromRgba(Rgba, Width, Height);
}

/// <summary>
/// 极简 PNG 解码器。共享层（<c>NegiCraftLauncher.Raster</c>）不能依赖 SkiaSharp，
/// 否则 WPF 侧又会把 11MB 的 <c>libSkiaSharp.dll</c> 拖回来，那就白迁了。
///
/// <para>覆盖范围：8/16 位、颜色类型 0(灰度)/2(RGB)/3(调色板)/4(灰度+alpha)/6(RGBA)、
/// 非隔行。Adam7 隔行图直接抛 <see cref="NotSupportedException"/> —— MC 皮肤与实例图标
/// 都不用它。</para>
/// </summary>
public static class PngCodec
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool LooksLikePng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(Signature);

    public static RawImage Decode(ReadOnlySpan<byte> bytes)
    {
        if (!LooksLikePng(bytes))
        {
            throw new InvalidDataException("不是 PNG 文件（签名不匹配）。");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        var idat = new MemoryStream();
        byte[]? palette = null;
        byte[]? transparency = null;
        var sawHeader = false;

        var pos = 8;
        while (pos + 8 <= bytes.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
            var type = bytes.Slice(pos + 4, 4);
            var dataStart = pos + 8;
            if (length < 0 || dataStart + length + 4 > bytes.Length) break;

            var data = bytes.Slice(dataStart, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
                height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];
                sawHeader = true;
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                transparency = data.ToArray();
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            pos = dataStart + length + 4;
        }

        if (!sawHeader || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("PNG 缺少有效的 IHDR。");
        }

        if (interlace != 0)
        {
            throw new NotSupportedException("不支持 Adam7 隔行 PNG。");
        }

        if (bitDepth is not (8 or 16))
        {
            throw new NotSupportedException($"不支持 {bitDepth} 位深的 PNG。");
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new NotSupportedException($"不支持的颜色类型 {colorType}。"),
        };

        var bytesPerPixel = Math.Max(1, channels * bitDepth / 8);
        var stride = (width * channels * bitDepth + 7) / 8;

        idat.Position = 0;
        byte[] raw;
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
        using (var plain = new MemoryStream())
        {
            zlib.CopyTo(plain);
            raw = plain.ToArray();
        }

        var expected = (long)(stride + 1) * height;
        if (raw.Length < expected)
        {
            throw new InvalidDataException($"PNG 数据不完整（期望 {expected} 字节，实际 {raw.Length}）。");
        }

        // 逐行反滤波：第 0 行与每行首个字节的上/左邻居都按 0 处理。
        var lines = new byte[height][];
        var previous = new byte[stride];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            var filter = raw[offset++];
            var line = new byte[stride];
            Array.Copy(raw, offset, line, 0, stride);
            offset += stride;
            Unfilter(filter, line, previous, bytesPerPixel);
            lines[y] = line;
            previous = line;
        }

        var rgba = new byte[width * height * 4];
        var sampleBytes = bitDepth / 8;

        for (var y = 0; y < height; y++)
        {
            var line = lines[y];
            for (var x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;

                switch (colorType)
                {
                    case 0:
                    {
                        var v = ReadSample(line, x * channels, sampleBytes);
                        r = g = b = v;
                        if (transparency is { Length: >= 2 } && v == transparency[1]) a = 0;
                        break;
                    }
                    case 2:
                    {
                        var i = x * channels * sampleBytes;
                        r = ReadSample(line, x * channels, sampleBytes);
                        g = line[i + sampleBytes];
                        b = line[i + sampleBytes * 2];
                        if (transparency is { Length: >= 6 } &&
                            r == transparency[1] && g == transparency[3] && b == transparency[5])
                        {
                            a = 0;
                        }

                        break;
                    }
                    case 3:
                    {
                        var index = line[x];
                        var p = index * 3;
                        if (palette is null || p + 2 >= palette.Length)
                        {
                            throw new InvalidDataException("调色板 PNG 缺少 PLTE 数据。");
                        }

                        r = palette[p];
                        g = palette[p + 1];
                        b = palette[p + 2];
                        if (transparency is not null && index < transparency.Length) a = transparency[index];
                        break;
                    }
                    case 4:
                    {
                        var i = x * channels * sampleBytes;
                        r = g = b = ReadSample(line, x * channels, sampleBytes);
                        a = line[i + sampleBytes];
                        break;
                    }
                    default: // 6
                    {
                        var i = x * channels * sampleBytes;
                        r = line[i];
                        g = line[i + sampleBytes];
                        b = line[i + sampleBytes * 2];
                        a = line[i + sampleBytes * 3];
                        break;
                    }
                }

                var o = (y * width + x) * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = a;
            }
        }

        return new RawImage(width, height, rgba);
    }

    /// <summary>
    /// 编码成 8 位 RGBA（颜色类型 6、行滤波 0）的 PNG。
    /// 预乘 BGRA 的 <see cref="PixelBuffer"/> 会先还原成直通 RGBA 再写。
    /// 主要用途是黄金参考图（T4 像素回归）与光栅化器的肉眼核对。
    /// </summary>
    public static byte[] Encode(PixelBuffer buffer)
    {
        var raw = new byte[buffer.Height * (1 + buffer.Width * 4)];
        var o = 0;
        for (var y = 0; y < buffer.Height; y++)
        {
            raw[o++] = 0; // 滤波类型 None
            for (var x = 0; x < buffer.Width; x++)
            {
                var c = buffer.Pixels[y * buffer.Width + x];
                var a = (c >> 24) & 0xFF;
                var r = (c >> 16) & 0xFF;
                var g = (c >> 8) & 0xFF;
                var b = c & 0xFF;

                if (a != 0 && a != 255)
                {
                    // 预乘 → 直通
                    r = Math.Min(255, r * 255 / a);
                    g = Math.Min(255, g * 255 / a);
                    b = Math.Min(255, b * 255 / a);
                }
                else if (a == 0)
                {
                    r = g = b = 0;
                }

                raw[o++] = (byte)r;
                raw[o++] = (byte)g;
                raw[o++] = (byte)b;
                raw[o++] = (byte)a;
            }
        }

        byte[] compressed;
        using (var output = new MemoryStream())
        {
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = output.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(Signature, 0, Signature.Length);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)buffer.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)buffer.Height);
        ihdr[8] = 8;  // 位深
        ihdr[9] = 6;  // 颜色类型 RGBA
        ihdr[10] = 0; // 压缩方法
        ihdr[11] = 0; // 滤波方法
        ihdr[12] = 0; // 非隔行
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IDAT"u8, compressed);
        WriteChunk(png, "IEND"u8, Array.Empty<byte>());

        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header.Slice(0, 4), (uint)data.Length);
        type.CopyTo(header.Slice(4, 4));
        stream.Write(header);

        if (data.Length > 0) stream.Write(data, 0, data.Length);

        var crc = Crc32(type, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>16 位样本取高字节（够用且不会溢出）。</summary>
    private static byte ReadSample(byte[] line, int sampleIndex, int sampleBytes) =>
        line[sampleIndex * sampleBytes];

    private static void Unfilter(byte filter, byte[] line, byte[] previous, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = bpp; i < line.Length; i++)
                {
                    line[i] = (byte)(line[i] + line[i - bpp]);
                }

                break;
            case 2:
                for (var i = 0; i < line.Length; i++)
                {
                    line[i] = (byte)(line[i] + previous[i]);
                }

                break;
            case 3:
                for (var i = 0; i < line.Length; i++)
                {
                    var left = i >= bpp ? line[i - bpp] : 0;
                    line[i] = (byte)(line[i] + ((left + previous[i]) >> 1));
                }

                break;
            case 4:
                for (var i = 0; i < line.Length; i++)
                {
                    var a = i >= bpp ? line[i - bpp] : 0;
                    var b = previous[i];
                    var c = i >= bpp ? previous[i - bpp] : 0;
                    line[i] = (byte)(line[i] + Paeth(a, b, c));
                }

                break;
            default:
                throw new InvalidDataException($"未知的 PNG 行滤波类型 {filter}。");
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }
}
