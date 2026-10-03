namespace NegiCraftLauncher.Raster;

/// <summary>
/// 一张已解码的皮肤贴图：<b>直通 alpha</b> 的 ARGB 像素，尺寸是原始尺寸（可能是 64x64、
/// 64x32、或 128x128 以上的高清皮肤）。
///
/// <para>这里的判定逻辑和 <c>libs/MinecraftSkinRender/SkinTypeChecker.cs</c> 是同一套算法，
/// 只是把 <c>SKBitmap.GetPixel</c> 换成了我们自己的 PNG 解码结果 —— 共享层不能依赖 SkiaSharp，
/// 否则 WPF 侧又会把 11MB 的 <c>libSkiaSharp.dll</c> 拖回来。</para>
/// </summary>
public sealed class SkinTexture
{
    private SkinTexture(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>直通 alpha，按 <c>0xAARRGGBB</c> 存放。</summary>
    public uint[] Pixels { get; }

    /// <summary>1.7 时代的老贴图：宽正好是高的两倍（64x32）。</summary>
    public bool IsLegacy => Width == Height * 2;

    /// <summary>解码失败时返回 null（调用方自己决定回退到什么）。</summary>
    public static SkinTexture? Decode(byte[] bytes)
    {
        try
        {
            var image = PngCodec.Decode(bytes);
            return FromRgba(image.Rgba, image.Width, image.Height);
        }
        catch
        {
            return null;
        }
    }

    public static SkinTexture FromRgba(byte[] rgba, int width, int height)
    {
        var pixels = new uint[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            var o = i * 4;
            pixels[i] = ((uint)rgba[o + 3] << 24) | ((uint)rgba[o] << 16) | ((uint)rgba[o + 1] << 8) | rgba[o + 2];
        }

        return new SkinTexture(width, height, pixels);
    }

    /// <summary>
    /// 归一化成渲染器要的 64x64 直通 ARGB 缓冲（只取左上角，超出的部分裁掉），
    /// 与旧版 <c>SkinService.LoadSkinPixelsFromBytes</c> 语义一致。
    /// </summary>
    public uint[] ToRenderPixels()
    {
        var pixels = new uint[64 * 64];
        var w = Math.Min(64, Width);
        var h = Math.Min(64, Height);

        for (var y = 0; y < h; y++)
        {
            Array.Copy(Pixels, y * Width, pixels, y * 64, w);
        }

        return pixels;
    }

    /// <summary>64x64 及以上且为方形时，判断是不是 Alex（细手臂）模型。</summary>
    public bool IsSlimSkin()
    {
        var scale = Width / 64;
        if (scale < 1) scale = 1;

        // 1.8+ 皮肤里，细手臂（Alex）会留空这几块手臂区域：
        //   右臂上/下 (50,16) 2x4、右臂身 (54,20) 2x12、左臂上/下 (42,48) 2x4、左臂身 (46,52) 2x12。
        // 宽手臂（Steve）这几块一定有实心手臂像素（alpha > 0）。
        var totalUnusedPixels = (2 * 4 + 2 * 12 + 2 * 4 + 2 * 12) * scale * scale;
        var transparentCount = 0;
        var dummyBlackCount = 0;
        var hasFullTransparentSubregion = false;

        CheckRegion(50 * scale, 16 * scale, 2 * scale, 4 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);
        CheckRegion(54 * scale, 20 * scale, 2 * scale, 12 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);
        CheckRegion(42 * scale, 48 * scale, 2 * scale, 4 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);
        CheckRegion(46 * scale, 52 * scale, 2 * scale, 12 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);

        // 1. 真·Steve 的掌心（42,48）从不会整块透明；只要有一块 100% 透明，就是 Alex。
        if (hasFullTransparentSubregion)
        {
            return true;
        }

        // 2. 不少皮肤站（如 Minotar）把没用的透明像素填成纯黑；透明+纯黑 >= 85% 也算 Alex。
        return transparentCount + dummyBlackCount >= totalUnusedPixels * 85 / 100;
    }

    /// <summary>
    /// 决定渲染模型。<b>只看贴图尺寸</b>：64x32 一律 <see cref="SkinFormat.Old"/>，
    /// 只有方形 64x64 及以上才用 <paramref name="isSlim"/> 区分 New / NewSlim。
    /// </summary>
    public SkinFormat ResolveFormat(bool isSlim)
    {
        if (IsLegacy) return SkinFormat.Old;
        if (Width < 64 || Height < 64 || Width != Height) return SkinFormat.Unknown;
        return isSlim ? SkinFormat.NewSlim : SkinFormat.New;
    }

    private void CheckRegion(
        int x, int y, int w, int h,
        ref int transparentCount, ref int dummyBlackCount, ref bool hasFullTransparentSubregion)
    {
        var regionTotal = w * h;
        var regionTrans = 0;

        for (var wi = 0; wi < w; wi++)
        {
            for (var hi = 0; hi < h; hi++)
            {
                var px = x + wi;
                var py = y + hi;
                if ((uint)px >= (uint)Width || (uint)py >= (uint)Height) continue;

                var c = Pixels[py * Width + px];
                var a = (c >> 24) & 0xFF;
                if (a == 0)
                {
                    transparentCount++;
                    regionTrans++;
                }
                else if (a == 255 && (c & 0x00FFFFFF) == 0)
                {
                    dummyBlackCount++;
                }
            }
        }

        if (regionTrans == regionTotal && regionTotal > 0)
        {
            hasFullTransparentSubregion = true;
        }
    }
}
