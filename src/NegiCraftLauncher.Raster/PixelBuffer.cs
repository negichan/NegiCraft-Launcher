using System.Runtime.CompilerServices;

namespace NegiCraftLauncher.Raster;

/// <summary>
/// 平台中立的位图：预乘 alpha 的 BGRA8888，行优先，左上原点。
///
/// <para>每个 <see cref="uint"/> 是 <c>0xAARRGGBB</c>（高位 alpha），在内存里按小端存放
/// 即字节序 B,G,R,A —— 正好是 Avalonia <c>PixelFormat.Bgra8888</c> 与 WPF
/// <c>PixelFormats.Pbgra32</c> 期待的内存布局。视图层拿到它以后只需要
/// <c>WritePixels</c>，不需要再做任何色彩换算。</para>
///
/// <para>注意区分：皮肤贴图的像素数组是 <b>直通 alpha</b>（和 Skia 解码结果一致），
/// 那是给光栅化器采样的输入；<see cref="PixelBuffer"/> 是 <b>预乘</b>，那是要直接上屏的。</para>
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Pixels = new uint[width * height];
    }

    public PixelBuffer(int width, int height, uint[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"像素数量应为 {width * height}，实际 {pixels.Length}。", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>预乘 BGRA，按 <c>0xAARRGGBB</c> 存放。</summary>
    public uint[] Pixels { get; }

    /// <summary>把一张直通 RGBA 的图转成预乘 BGRA 的 <see cref="PixelBuffer"/>。</summary>
    public static PixelBuffer FromRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("RGBA 数据长度不足。", nameof(rgba));
        }

        var buffer = new PixelBuffer(width, height);
        var dst = buffer.Pixels;

        for (var i = 0; i < dst.Length; i++)
        {
            var o = i * 4;
            uint r = rgba[o];
            uint g = rgba[o + 1];
            uint b = rgba[o + 2];
            uint a = rgba[o + 3];

            if (a == 0)
            {
                dst[i] = 0;
                continue;
            }

            if (a != 255)
            {
                // 预乘：分量各自乘 alpha 再四舍五入。
                r = (r * a + 127) / 255;
                g = (g * a + 127) / 255;
                b = (b * a + 127) / 255;
            }

            dst[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }

        return buffer;
    }

    /// <summary>
    /// 把一张<b>直通</b>（非预乘）BGRA —— 字节序 B,G,R,A —— 转成预乘 BGRA。
    ///
    /// <para>两端解码器都归一到这个输入格式（WPF <c>PixelFormats.Bgra32</c>、
    /// Avalonia <c>PixelFormat.Bgra8888</c> + 非预乘），于是"直通 → 预乘"只发生在共享层这一处，
    /// 两端不可能算出两种结果。壁纸几乎都是不透明的，所以 <c>a != 255</c> 那条分支基本不会走。</para>
    /// </summary>
    public static PixelBuffer FromBgra(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("BGRA 数据长度不足。", nameof(bgra));
        }

        var buffer = new PixelBuffer(width, height);
        var dst = buffer.Pixels;

        for (var i = 0; i < dst.Length; i++)
        {
            var o = i * 4;
            uint b = bgra[o];
            uint g = bgra[o + 1];
            uint r = bgra[o + 2];
            uint a = bgra[o + 3];

            if (a == 0)
            {
                dst[i] = 0;
                continue;
            }

            if (a != 255)
            {
                b = (b * a + 127) / 255;
                g = (g * a + 127) / 255;
                r = (r * a + 127) / 255;
            }

            dst[i] = (a << 24) | (r << 16) | (g << 8) | b;
        }

        return buffer;
    }

    /// <summary>
    /// 把一份<b>已经是预乘</b> BGRA（字节序 B,G,R,A）的缓冲原样装进 <see cref="PixelBuffer"/>：
    /// 只做字节拼装，不碰 alpha。
    ///
    /// <para>Avalonia 的 <c>PixelFormats.Bgra8888</c> 就是预乘的，它的 <c>CopyPixels</c> 交出来
    /// 直接能用；WPF 那边给的是 <c>Bgra32</c>（直通），得走 <see cref="FromBgra"/> 乘一次。
    /// 两个入口分开，是为了让"哪一端负责乘 alpha"这件事在类型上就写得清楚 —— 乘两遍会让半透明
    /// 像素系统性偏暗（<c>AvatarComposer</c> 那边栽过一次）。</para>
    /// </summary>
    public static PixelBuffer FromPbgra(ReadOnlySpan<byte> pbgra, int width, int height)
    {
        if (pbgra.Length < width * height * 4)
        {
            throw new ArgumentException("BGRA 数据长度不足。", nameof(pbgra));
        }

        var buffer = new PixelBuffer(width, height);
        pbgra[..(width * height * 4)]
            .CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.Pixels.AsSpan()));
        return buffer;
    }

    /// <summary>
    /// 长边超过 <paramref name="maxLongEdge"/> 时按比例盒式缩小（整数平均），否则原样返回。
    ///
    /// <para><b>为什么在共享层做而不是交给各端解码器缩</b>：WPF 能"解码时缩"（DCT 域降采样），
    /// Avalonia 12 的 <c>Bitmap</c> 根本没有按目标尺寸解码的重载 ⇒ 一边缩一边不缩，同一张图两端
    /// 就不是同一份像素，调色再准也对不齐。放在这里两边逐位一致。</para>
    ///
    /// <para>盒式（不做滤波）是有意的：这张图最终只会以 1180 宽、还可能糊着当背景用，
    /// 缩到 2560 这一步的质量差异看不出来，而实现只要十几行。</para>
    /// </summary>
    public static PixelBuffer Downsample(PixelBuffer source, int maxLongEdge)
    {
        var longEdge = Math.Max(source.Width, source.Height);
        if (maxLongEdge <= 0 || longEdge <= maxLongEdge) return source;

        var scale = maxLongEdge / (double)longEdge;
        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var src = source.Pixels;
        var dst = new uint[w * h];

        for (var y = 0; y < h; y++)
        {
            var y0 = y * source.Height / h;
            var y1 = Math.Max(y0 + 1, (y + 1) * source.Height / h);
            for (var x = 0; x < w; x++)
            {
                var x0 = x * source.Width / w;
                var x1 = Math.Max(x0 + 1, (x + 1) * source.Width / w);

                uint b = 0, g = 0, r = 0, a = 0;
                var n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    var row = sy * source.Width;
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var p = src[row + sx];
                        b += p & 0xFF;
                        g += (p >> 8) & 0xFF;
                        r += (p >> 16) & 0xFF;
                        a += p >> 24;
                        n++;
                    }
                }

                // 四舍五入而不是整除截断：整体缩一档会让壁纸偏暗一点。
                // uint + int 在 C# 里升到 long，所以每个通道都要显式落回 uint 再拼。
                dst[y * w + x] = ((uint)((a + n / 2) / n) << 24) | ((uint)((r + n / 2) / n) << 16) |
                                 ((uint)((g + n / 2) / n) << 8) | (uint)((b + n / 2) / n);
            }
        }

        return new PixelBuffer(w, h, dst);
    }

    /// <summary>单色不透明填充，用于占位图。</summary>
    public static PixelBuffer Solid(int width, int height, uint argb)
    {
        var buffer = new PixelBuffer(width, height);
        buffer.Pixels.AsSpan().Fill(argb);
        return buffer;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int x, int y, uint argb)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        Pixels[y * Width + x] = argb;
    }
}
