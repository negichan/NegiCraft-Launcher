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
    private static readonly uint[][] Pal =
    {
        // grass: ["#5b8f3a","#6aa84f","#4e7a32","#7cbd5a"]
        new uint[] { 0xFF5B8F3A, 0xFF6AA84F, 0xFF4E7A32, 0xFF7CBD5A },
        // stone: ["#7f7f7f","#8f8f8f","#6e6e6e","#9a9a9a"]
        new uint[] { 0xFF7F7F7F, 0xFF8F8F8F, 0xFF6E6E6E, 0xFF9A9A9A },
        // tnt: ["#c0392b","#e74c3c","#a93226","#d35445"]
        new uint[] { 0xFFC0392B, 0xFFE74C3C, 0xFFA93226, 0xFFD35445 },
        // dirt: ["#8b5a2b","#9b6a3b","#7a4a1f","#a87442"]
        new uint[] { 0xFF8B5A2B, 0xFF9B6A3B, 0xFF7A4A1F, 0xFFA87442 },
        // log: ["#6b4f2a","#7a5c33","#5c4322","#8a6a3c"]
        new uint[] { 0xFF6B4F2A, 0xFF7A5C33, 0xFF5C4322, 0xFF8A6A3C },
    };

    public static PixelBuffer CreateBlock(string type)
    {
        var bmp = new PixelBuffer(8, 8);

        long seed = type.Length * 97 + 7;
        double Rnd()
        {
            seed = (seed * 16807) % 2147483647;
            return (double)seed / 2147483647.0;
        }

        var p = type switch
        {
            "grass" => Pal[0],
            "stone" => Pal[1],
            "tnt" => Pal[2],
            "dirt" => Pal[3],
            "log" => Pal[4],
            _ => Pal[3],
        };
        var dirtPal = Pal[3];

        for (var i = 0; i < 8; i++) // X column
        {
            for (var j = 0; j < 8; j++) // Y row
            {
                var col = p[(int)(Rnd() * 4)];
                if (type == "grass" && j > 2)
                {
                    col = dirtPal[(int)(Rnd() * 4)];
                }

                if (type == "tnt" && (j == 3 || j == 4))
                {
                    col = i % 2 == 1 ? 0xFF2A2A2A : 0xFFEEEEEE;
                }

                if (type == "log" && (i == 0 || i == 7))
                {
                    col = 0xFF4A3519;
                }

                bmp.Pixels[j * 8 + i] = col;
            }
        }

        return bmp;
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
