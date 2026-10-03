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
