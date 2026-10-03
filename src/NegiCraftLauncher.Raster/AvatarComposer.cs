namespace NegiCraftLauncher.Raster;

/// <summary>
/// 头像合成：把 8x8 的脸与 8x8 的帽子/头发层按 1:1 像素对齐叠在一起，
/// 再用最近邻放大 8 倍成 64x64。这样 Alex 和自定义皮肤的脸与头发之间不会错位撕裂。
///
/// <para>输入是 64x64 直通 ARGB 的皮肤像素（<see cref="SkinTexture.ToRenderPixels"/> 的输出），
/// 输出是可直接上屏的预乘 <see cref="PixelBuffer"/>。</para>
/// </summary>
public static class AvatarComposer
{
    public const int Size = 64;

    public static PixelBuffer Create(uint[] skinPixels)
    {
        var bmp = new PixelBuffer(Size, Size);
        var ptr = bmp.Pixels;

        // 1. 合成 8x8 的脸 + 帽子层
        Span<uint> head8 = stackalloc uint[64];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var face = skinPixels[(8 + y) * 64 + (8 + x)];
                var hat = skinPixels[(8 + y) * 64 + (40 + x)];
                var hatA = (hat >> 24) & 0xFF;

                if (hatA == 0)
                {
                    head8[y * 8 + x] = face;
                }
                else if (hatA == 255)
                {
                    head8[y * 8 + x] = hat;
                }
                else
                {
                    var faceA = (face >> 24) & 0xFF;
                    var outA = hatA + faceA * (255 - hatA) / 255;
                    var outR = (((hat >> 16) & 0xFF) * hatA + ((face >> 16) & 0xFF) * (255 - hatA)) / 255;
                    var outG = (((hat >> 8) & 0xFF) * hatA + ((face >> 8) & 0xFF) * (255 - hatA)) / 255;
                    var outB = ((hat & 0xFF) * hatA + (face & 0xFF) * (255 - hatA)) / 255;
                    head8[y * 8 + x] = (outA << 24) | (outR << 16) | (outG << 8) | outB;
                }
            }
        }

        // 2. 8x8 → 64x64 最近邻放大
        for (var y = 0; y < Size; y++)
        {
            var srcY = y / 8;
            for (var x = 0; x < Size; x++)
            {
                ptr[y * Size + x] = head8[srcY * 8 + x / 8];
            }
        }

        return bmp;
    }
}
