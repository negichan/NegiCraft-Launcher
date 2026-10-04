namespace NegiCraftLauncher.Raster;

/// <summary>
/// 头像合成：把皮肤正面那张 8x8 的脸（贴图 (8,8)）与 8x8 的帽子/头发层（贴图 (40,8)）
/// 画成一个 64x64 的头部头像。
///
/// <para><b>两层不是等大的</b>：脸按 48px（每边内缩 8）、帽子层按 56px（每边内缩 4）画。
/// 帽子层比脸大一圈，于是它在四个方向上各溢出 4px，形成"整个头"的圆润轮廓；
/// 等大叠合会把帽子层拍平贴在脸上，看起来只剩一张正脸 —— 这是刻意保留的观感。</para>
///
/// <para>输入是 64x64 <b>直通 alpha</b> 的皮肤像素（<see cref="SkinTexture.ToRenderPixels"/> 的输出），
/// 输出是可直接上屏的<b>预乘</b> <see cref="PixelBuffer"/>。</para>
/// </summary>
public static class AvatarComposer
{
    /// <summary>头像边长。</summary>
    public const int Size = 64;

    /// <summary>脸层边长。内缩 <c>(64 - 48) / 2 = 8</c>，放大 6 倍。</summary>
    private const int FaceSize = 48;

    /// <summary>帽子层边长。内缩 <c>(64 - 56) / 2 = 4</c>，放大 7 倍 —— 比脸外扩 4px。</summary>
    private const int HatSize = 56;

    public static PixelBuffer Create(uint[] skinPixels)
    {
        var bmp = new PixelBuffer(Size, Size);
        var dst = bmp.Pixels;

        // 脸先落地（不混合），帽子层再盖上去（预乘 src-over）。顺序不能反。
        Blot(dst, skinPixels, srcX: 8, offset: (Size - FaceSize) / 2, scale: FaceSize / 8, blend: false);
        Blot(dst, skinPixels, srcX: 40, offset: (Size - HatSize) / 2, scale: HatSize / 8, blend: true);

        return bmp;
    }

    /// <summary>
    /// 把贴图里以 <paramref name="srcX"/> 为左边界的那 8x8 方块（行固定从 8 开始，即头部正面）
    /// 按 <paramref name="scale"/> 倍最近邻放大，写到 <paramref name="dst"/> 的
    /// <paramref name="offset"/> 处。
    /// </summary>
    private static void Blot(uint[] dst, uint[] src, int srcX, int offset, int scale, bool blend)
    {
        for (var py = 0; py < 8; py++)
        {
            for (var px = 0; px < 8; px++)
            {
                var col = src[(8 + py) * 64 + (srcX + px)];
                var a = (col >> 24) & 0xFF;
                if (a == 0) continue;

                // 贴图是直通 alpha，PixelBuffer 要的是预乘：先在这里把分量乘上 alpha。
                var sR = ((col >> 16) & 0xFF) * a / 255;
                var sG = ((col >> 8) & 0xFF) * a / 255;
                var sB = (col & 0xFF) * a / 255;

                for (var y = 0; y < scale; y++)
                {
                    var dy = offset + py * scale + y;
                    for (var x = 0; x < scale; x++)
                    {
                        var dx = offset + px * scale + x;
                        var i = dy * Size + dx;

                        if (!blend)
                        {
                            dst[i] = (a << 24) | (sR << 16) | (sG << 8) | sB;
                            continue;
                        }

                        // 预乘 src-over：源已经是预乘的，所以颜色直接相加、不用再乘一遍 alpha。
                        var d = dst[i];
                        var inverse = 255 - a;
                        var r = sR + ((d >> 16) & 0xFF) * inverse / 255;
                        var g = sG + ((d >> 8) & 0xFF) * inverse / 255;
                        var b = sB + (d & 0xFF) * inverse / 255;
                        var outA = a + (d >> 24) * inverse / 255;

                        dst[i] = (outA << 24)
                               | (Math.Min(r, 255u) << 16)
                               | (Math.Min(g, 255u) << 8)
                               | Math.Min(b, 255u);
                    }
                }
            }
        }
    }
}
