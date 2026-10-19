namespace NegiCraftLauncher.Raster;

/// <summary>
/// 程序化像素画：方块图标、首页背景场景。纯计算，和 UI 框架无关，
/// 输出预乘 BGRA 的 <see cref="PixelBuffer"/>，由各平台视图层自己转成 Bitmap。
///
/// <para>这段是从 Avalonia 版 <c>App/Services/PixelArtService.cs</c> 逐行搬过来的，
/// 包括随机数种子与绘制顺序，保证像素级一致。</para>
/// </summary>
public static class PixelArt
{
    // MC 的调色板（平原生物群系的草、泥土、石头、TNT、原木）。
    // 这些值照真方块的平均色取，不是随手调的 —— 换成别的绿或棕，一眼就不像 MC 了。
    private static readonly uint[] GrassPal = { 0xFF91BD59, 0xFF7FB84E, 0xFF9CCB63, 0xFF86B44F };
    private static readonly uint[] DirtPal = { 0xFF866043, 0xFF79553A, 0xFF966F4F, 0xFF6E4A31 };
    private static readonly uint[] StonePal = { 0xFF7F7F7F, 0xFF8A8A8A, 0xFF737373, 0xFF949494 };
    private static readonly uint[] TntPal = { 0xFFDB5B3F, 0xFFE04F35, 0xFFC94C33, 0xFFD9674C };
    private static readonly uint[] LogPal = { 0xFF6A5030, 0xFF8A6A3C, 0xFF5A4428, 0xFF9B7A48 };

    /// <summary>
    /// 一块方块的纹理（16x16 —— MC 的原生分辨率）。
    ///
    /// <para><b>顶面和侧面是两张不同的图</b>：grass 的顶面是纯草绿，侧面是「上面几行草
    /// + 参差的草边 + 下面泥土」。三个面共用一张纹理的话，立方体的顶面会压着一条泥土色横边，
    /// 一眼就假 —— 这一处是「像 MC 方块」和「像一张噪点贴图」的分界。</para>
    /// </summary>
    private static PixelBuffer BlockTexture(string type, bool top)
    {
        const int n = 16;
        var bmp = new PixelBuffer(n, n);
        var ptr = bmp.Pixels;

        long seed = type.Length * 97 + (top ? 7 : 13);
        double Rnd()
        {
            seed = (seed * 16807) % 2147483647;
            return (double)seed / 2147483647.0;
        }

        uint Pick(uint[] pal) => pal[(int)(Rnd() * pal.Length)];

        var main = type switch
        {
            "grass" => GrassPal,
            "stone" => StonePal,
            "tnt" => TntPal,
            "log" => LogPal,
            _ => DirtPal,
        };

        // 侧面草边的深度：每列 3~6 行、每列不同 —— 真 grass_block_side 就是这种毛边，
        // 一条直线会立刻暴露是画出来的。
        var grassDepth = new int[n];
        for (var i = 0; i < n; i++) grassDepth[i] = 3 + (int)(Rnd() * 4);

        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                uint col;

                if (type == "grass")
                {
                    col = top || y < grassDepth[x] ? Pick(GrassPal) : Pick(DirtPal);
                }
                else if (type == "log")
                {
                    // 竖纹：每 4 列一组深浅，偶尔插一列结节。
                    col = LogPal[(x / 4 + (Rnd() < 0.08 ? 1 : 0)) % LogPal.Length];
                }
                else if (type == "tnt")
                {
                    // 红底 + 中间一条白带。真 TNT 的白带上是黑字，16px 画不下，
                    // 用两道深色竖条示意。
                    col = y is >= 6 and <= 9
                        ? (x % 6 is 2 or 3 ? 0xFF2B2B2B : 0xFFE8E8E8)
                        : Pick(TntPal);
                }
                else
                {
                    col = Pick(main);
                }

                ptr[y * n + x] = col;
            }
        }

        return bmp;
    }

    /// <summary>
    /// 等距立方体图标 —— PCL / HMCL 那种「一块方块摆在眼前」的样子，而不是一张平铺贴图。
    ///
    /// <para><b>几何</b>：2:1 等距投影，立方体正好吃满 size×size。四个关键点
    /// <c>T(size/2,0) R(size,size/4) B(size/2,size/2) L(0,size/4)</c>，
    /// 顶面是 T-R-B-L 那个菱形，左面从 L 往右下铺、右面从 B 往右上铺，各占一半高度。
    /// 每个屏幕像素反解 (u,v) 再采纹理，比正向画三个平行四边形少一层边界接缝。</para>
    ///
    /// <para><b>明暗</b>：光源在左上，顶 1.0 / 左 0.78 / 右 0.62 —— 和 MC 的物品图标
    /// 同一个方向感。这一层是「看着像方块」而不是「看着像贴纸」的关键，别去掉。</para>
    ///
    /// <para>顶面采 top 版、左右面采 side 版（草方块因此只有侧面才有土色）。</para>
    /// </summary>
    public static PixelBuffer CreateBlockCube(string type, int size = 32) =>
        CreateBlockCube(BlockTexture(type, top: true), BlockTexture(type, top: false), size);

    /// <summary>
    /// 用<b>两张现成的纹理</b>拼等距立方体 —— 游戏 jar 里的真方块贴图就走这条路
    /// （纹理从 <c>assets/minecraft/textures/block/*.png</c> 读出来，原样 16x16）。
    ///
    /// <para>纹理尺寸不限于 16 —— 采样按 <c>tex.Width/Height</c> 归一化，32x32 的高清包
    /// 或 8x8 的老资源包都能直接喂进来。</para>
    /// </summary>
    public static PixelBuffer CreateBlockCube(PixelBuffer topTex, PixelBuffer sideTex, int size = 32)
    {
        var bmp = new PixelBuffer(size, size);
        var ptr = bmp.Pixels;

        double s = size;
        var half = s / 2;
        var quarter = s / 4;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // 取像素中心再反解，避免斜边整像素错位。
                double px = x + 0.5, py = y + 0.5;
                double u, v, shade;
                PixelBuffer tex;

                // 顶面：u 沿「右下」，v 沿「左下」。
                var topU = ((px - half) / half + py / quarter) / 2;
                var topV = (py / quarter - (px - half) / half) / 2;
                if (topU is >= 0 and <= 1 && topV is >= 0 and <= 1)
                {
                    u = topU;
                    v = topV;
                    shade = 1.0;
                    tex = topTex;
                }
                else if (px < half)
                {
                    // 左面：u 沿 +x 往下走，v 是纯垂直方向。
                    u = px / half;
                    v = (py - quarter - quarter * u) / half;
                    shade = 0.78;
                    if (u is < 0 or > 1 || v is < 0 or > 1) continue;
                    tex = sideTex;
                }
                else
                {
                    // 右面：和左面镜像，u 从立方体的中脊往右走。
                    u = (px - half) / half;
                    v = (py - half + quarter * u) / half;
                    shade = 0.62;
                    if (u is < 0 or > 1 || v is < 0 or > 1) continue;
                    tex = sideTex;
                }

                var tx = Math.Min(tex.Width - 1, (int)(u * tex.Width));
                var ty = Math.Min(tex.Height - 1, (int)(v * tex.Height));
                ptr[y * size + x] = Shade(tex.Pixels[ty * tex.Width + tx], shade);
            }
        }

        return bmp;
    }

    /// <summary>按系数压暗 RGB，alpha 保持不变 —— 三个面靠这个拉开层次。</summary>
    private static uint Shade(uint col, double k)
    {
        if (k >= 1.0) return col;
        var r = (uint)(((col >> 16) & 0xFF) * k);
        var g = (uint)(((col >> 8) & 0xFF) * k);
        var b = (uint)((col & 0xFF) * k);
        return (col & 0xFF000000u) | (r << 16) | (g << 8) | b;
    }

    public static PixelBuffer CreateBackground(bool dark)
    {
        const int w = 240;
        const int h = 146;
        var bmp = new PixelBuffer(w, h);
        var ptr = bmp.Pixels;

        long seed = 7;
        double Rnd()
        {
            seed = (seed * 16807) % 2147483647;
            return (double)seed / 2147483647.0;
        }

        // 1. Sky gradient
        for (var y = 0; y < h; y++)
        {
            var t = (double)y / h;
            uint col;
            if (dark)
            {
                if (t < 0.6)
                {
                    col = LerpColor(0xFF0B1026, 0xFF24305E, t / 0.6);
                }
                else
                {
                    col = LerpColor(0xFF24305E, 0xFF3A3F6B, (t - 0.6) / 0.4);
                }
            }
            else
            {
                if (t < 0.6)
                {
                    col = LerpColor(0xFF6FB6F2, 0xFFA9D8F7, t / 0.6);
                }
                else
                {
                    col = LerpColor(0xFFA9D8F7, 0xFFD9EEFB, (t - 0.6) / 0.4);
                }
            }

            for (var x = 0; x < w; x++)
            {
                SetPixel(ptr, w, h, x, y, col);
            }
        }

        // 2. Stars & moon (dark) or sun & clouds (light)
        if (dark)
        {
            for (var i = 0; i < 70; i++)
            {
                var sx = (int)(Rnd() * w);
                var sy = (int)(Rnd() * h * 0.55);
                var alpha = 0.3 + Rnd() * 0.7;
                var starCol = ((uint)(alpha * 255) << 24) | 0x00FFFFFF;
                SetPixelBlend(ptr, w, h, sx, sy, starCol);
            }

            // Moon at (206, 10), 9x9 pixels
            FillRect(ptr, w, h, 206, 10, 9, 9, 0xFFE9E6D6);
            FillRect(ptr, w, h, 208, 12, 2, 2, 0xFFCFCBB8);
            FillRect(ptr, w, h, 212, 15, 2, 2, 0xFFCFCBB8);
        }
        else
        {
            FillRect(ptr, w, h, 112, 12, 12, 12, 0xFFFFF7C2);

            var clouds = new[]
            {
                (70, 18, 26, 4),
                (78, 14, 12, 4),
                (150, 28, 30, 4),
                (160, 24, 14, 4),
                (205, 12, 22, 4),
            };
            foreach (var (cx, cy, cw, ch) in clouds)
            {
                FillRect(ptr, w, h, cx, cy, cw, ch, 0xEBFFFFFF);
            }
        }

        // 3. Ridges (mountains)
        DrawRidge(ptr, w, h, 78, 4, dark ? 0xFF2B3460 : 0xFF8FB4D6, 3);
        DrawRidge(ptr, w, h, 90, 5, dark ? 0xFF1F2A4A : 0xFF6F9F8A, 11);

        // 4. Ground
        long groundSeed = 7;
        double GroundRnd()
        {
            groundSeed = (groundSeed * 16807) % 2147483647;
            return (double)groundSeed / 2147483647.0;
        }

        var groundY = 104;
        for (var x = 0; x < w; x++)
        {
            if (x % 6 == 0) groundY += (int)((GroundRnd() - 0.5) * 3);
            groundY = Math.Max(98, Math.Min(110, groundY));

            FillRect(ptr, w, h, x, groundY, 1, 2, dark ? 0xFF1F5130 : 0xFF5FA63A);
            FillRect(ptr, w, h, x, groundY + 2, 1, 8, dark ? 0xFF2A1D12 : 0xFF7A5230);
            FillRect(ptr, w, h, x, groundY + 10, 1, h - (groundY + 10), dark ? 0xFF1D1D22 : 0xFF6D6D6D);
        }

        // 5. Trees
        var trees = new[] { (18, 4), (34, 5), (120, 4), (136, 6), (176, 5) };
        foreach (var (tx, ts) in trees)
        {
            const int ty = 96;
            FillRect(ptr, w, h, tx, ty - ts * 2, 2, ts * 2 + 8, dark ? 0xFF2D1F12 : 0xFF5A3D22);
            FillRect(ptr, w, h, tx - ts, ty - ts * 3, ts * 2 + 2, ts * 2, dark ? 0xFF173B24 : 0xFF3E7F2C);
            FillRect(ptr, w, h, tx - ts + 2, ty - ts * 4, ts * 2 - 2, ts, dark ? 0xFF173B24 : 0xFF3E7F2C);
        }

        return bmp;
    }

    private static void DrawRidge(uint[] ptr, int w, int h, int baseHeight, int amp, uint col, long seed)
    {
        double Rnd()
        {
            seed = (seed * 16807) % 2147483647;
            return (double)seed / 2147483647.0;
        }

        var currentH = baseHeight;
        for (var x = 0; x < w; x += 2)
        {
            currentH += (int)((Rnd() - 0.5) * amp);
            currentH = Math.Max(baseHeight - 14, Math.Min(baseHeight + 8, currentH));
            FillRect(ptr, w, h, x, currentH, 2, h - currentH, col);
        }
    }

    private static void FillRect(uint[] ptr, int w, int h, int rx, int ry, int rw, int rh, uint col)
    {
        for (var y = ry; y < ry + rh; y++)
        {
            if (y < 0 || y >= h) continue;
            for (var x = rx; x < rx + rw; x++)
            {
                if (x < 0 || x >= w) continue;
                SetPixel(ptr, w, h, x, y, col);
            }
        }
    }

    private static void SetPixel(uint[] ptr, int w, int h, int x, int y, uint col)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        // 位图是 Bgra8888：uint 取值保持 0xAARRGGBB，小端落盘后字节序正好是 B,G,R,A。
        ptr[y * w + x] = col;
    }

    private static void SetPixelBlend(uint[] ptr, int w, int h, int x, int y, uint col)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        var a = (col >> 24) & 0xFF;
        if (a >= 255)
        {
            SetPixel(ptr, w, h, x, y, col);
            return;
        }

        var existing = ptr[y * w + x];
        var exR = (existing >> 16) & 0xFF;
        var exG = (existing >> 8) & 0xFF;
        var exB = existing & 0xFF;

        var inR = (col >> 16) & 0xFF;
        var inG = (col >> 8) & 0xFF;
        var inB = col & 0xFF;

        var outR = (inR * a + exR * (255 - a)) / 255;
        var outG = (inG * a + exG * (255 - a)) / 255;
        var outB = (inB * a + exB * (255 - a)) / 255;

        ptr[y * w + x] = 0xFF000000u | (outR << 16) | (outG << 8) | outB;
    }

    private static uint LerpColor(uint c1, uint c2, double t)
    {
        t = Math.Max(0.0, Math.Min(1.0, t));
        uint r1 = (c1 >> 16) & 0xFF, g1 = (c1 >> 8) & 0xFF, b1 = c1 & 0xFF;
        uint r2 = (c2 >> 16) & 0xFF, g2 = (c2 >> 8) & 0xFF, b2 = c2 & 0xFF;

        var r = (uint)(r1 + (r2 - r1) * t);
        var g = (uint)(g1 + (g2 - g1) * t);
        var b = (uint)(b1 + (b2 - b1) * t);

        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }
}
