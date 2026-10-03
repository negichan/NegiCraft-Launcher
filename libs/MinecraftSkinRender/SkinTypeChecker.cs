using SkiaSharp;

namespace MinecraftSkinRender;

public static class SkinTypeChecker
{
    /// <summary>
    /// 获取皮肤类型
    /// </summary>
    /// <param name="image">图片</param>
    /// <returns>类型</returns>
    public static SkinType GetTextType(SKBitmap image)
    {
        if (image.Width >= 64 && image.Height >= 64 && image.Width == image.Height)
        {
            if (IsSlimSkin(image))
            {
                return SkinType.NewSlim;
            }
            else
            {
                return SkinType.New;
            }
        }
        else if (image.Width == image.Height * 2)
        {
            return SkinType.Old;
        }
        else
        {
            return SkinType.Unkonw;
        }
    }

    /// <summary>
    /// 是否为1.8新版皮肤 (Slim/Alex)
    /// </summary>
    /// <param name="image">图片</param>
    /// <returns></returns>
    public static bool IsSlimSkin(SKBitmap image)
    {
        var scale = image.Width / 64;
        if (scale < 1) scale = 1;

        // In the Minecraft 1.8+ skin format, slim (Alex) skins leave these arm regions unused:
        // 1. Right arm top/bottom unused: (50, 16) 2x4
        // 2. Right arm body unused: (54, 20) 2x12
        // 3. Left arm top/bottom unused: (42, 48) 2x4
        // 4. Left arm body unused: (46, 52) 2x12
        // In Steve (classic) skins, these regions contain solid arm pixels (Alpha > 0).
        int totalUnusedPixels = (2 * 4 + 2 * 12 + 2 * 4 + 2 * 12) * scale * scale; // 64 * scale^2
        int transparentCount = 0;
        int dummyBlackCount = 0;
        bool hasFullTransparentSubregion = false;

        CheckRegion(image, 50 * scale, 16 * scale, 2 * scale, 4 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);
        CheckRegion(image, 54 * scale, 20 * scale, 2 * scale, 12 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);
        CheckRegion(image, 42 * scale, 48 * scale, 2 * scale, 4 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);
        CheckRegion(image, 46 * scale, 52 * scale, 2 * scale, 12 * scale, ref transparentCount, ref dummyBlackCount, ref hasFullTransparentSubregion);

        // 1. In genuine Steve (classic) skins, base layer limb patches (especially the palm at (42,48))
        // are never fully transparent. If any of the unused patches is 100% transparent, it's Alex.
        if (hasFullTransparentSubregion)
        {
            return true;
        }

        // 2. Many editors or skin mirrors (e.g. Minotar) fill unused transparent pixels with solid black.
        // If >= 85% of these unused pixels are either transparent or solid black padding, it's Alex.
        int unusedCount = transparentCount + dummyBlackCount;
        return unusedCount >= (totalUnusedPixels * 85 / 100);
    }

    private static void CheckRegion(
        SKBitmap image, int x, int y, int w, int h,
        ref int transparentCount, ref int dummyBlackCount, ref bool hasFullTransparentSubregion)
    {
        int regionTotal = w * h;
        int regionTrans = 0;

        for (int wi = 0; wi < w; wi++)
        {
            for (int hi = 0; hi < h; hi++)
            {
                var c = image.GetPixel(x + wi, y + hi);
                if (c.Alpha == 0)
                {
                    transparentCount++;
                    regionTrans++;
                }
                else if (c.Alpha == 255 && c.Red == 0 && c.Green == 0 && c.Blue == 0)
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
