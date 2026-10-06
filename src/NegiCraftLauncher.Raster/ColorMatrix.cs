namespace NegiCraftLauncher.Raster;

/// <summary>
/// 4×5 色彩矩阵（行主序 <c>float[20]</c>，行向量约定 <c>out = in · M</c>，
/// <c>in = (r,g,b,a,1)</c>），用来做壁纸调色：对比度 → 饱和度 → 色相。
///
/// <para><b>第 5 列（偏移）已经乘过 255</b>，所以 <see cref="ImageGrade"/> 可以直接在
/// 0..255 的整数分量上算，省掉每像素两次归一化。</para>
///
/// <para><b>luma 常数用 0.213 / 0.715 / 0.072</b>（SVG <c>feColorMatrix</c> 那组），
/// 不用 Rec.709 的 0.2126/0.7152/0.0722：色相旋转矩阵的 YIQ 交叉项是从这组常数推出来的，
/// 两套混用会让"饱和度"和"色相"对灰点的定义不一致。两者相差 0.2%，肉眼无感。</para>
///
/// <para><b>空域</b>：全部在 8-bit gamma 空间计算，不做线性化 —— 调色是"给照片改个口味"，
/// 不是物理正确的重着色，线性化反而会让人觉得颜色发灰。视频壁纸<b>刻意不吃调色</b>
/// （只共享取景），所以这里没有第二个实现要与之对齐。</para>
/// </summary>
public static class ColorMatrix
{
    private const double Lr = 0.213, Lg = 0.715, Lb = 0.072;

    /// <summary>单位矩阵。参数全为 0 时 <see cref="Build"/> 必须精确落在这组值上。</summary>
    public static readonly float[] Identity =
    [
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 1, 0,
    ];

    /// <summary>三个参数都是"滑块值"：对比度/饱和度 −100..100（0 = 不变），色相 −180..180 度。</summary>
    public static float[] Build(double contrast, double saturation, double hueDegrees)
    {
        var m = Contrast(1 + contrast / 100d);
        m = Multiply(m, Saturation(1 + saturation / 100d));
        m = Multiply(m, Hue(hueDegrees * Math.PI / 180d));
        return To4x5(m);
    }

    /// <summary>是否等价于单位矩阵（用于"没调色就别碰像素"的直通判断）。</summary>
    public static bool IsIdentity(float[] m)
    {
        for (var i = 0; i < Identity.Length; i++)
        {
            if (Math.Abs(m[i] - Identity[i]) > 1e-6f) return false;
        }

        return true;
    }

    // 内部一律用 5×5（行主序 float[25]）做乘法，最后砍掉恒等的 alpha 行。

    private static float[] Contrast(double s)
    {
        // out = (in - 0.5) * s + 0.5  ⇒ 对角 s，偏移 0.5*(1-s)（第 5 列要 ×255）。
        var offset = (float)(127.5 * (1 - s));
        return [
            (float)s, 0, 0, 0, offset,
            0, (float)s, 0, 0, offset,
            0, 0, (float)s, 0, offset,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1,
        ];
    }

    private static float[] Saturation(double s)
    {
        static float Keep(double luma, double amount) => (float)(luma + (1 - luma) * amount);
        static float Share(double luma, double amount) => (float)(luma * (1 - amount));

        return [
            Keep(Lr, s), Share(Lg, s), Share(Lb, s), 0, 0,
            Share(Lr, s), Keep(Lg, s), Share(Lb, s), 0, 0,
            Share(Lr, s), Share(Lg, s), Keep(Lb, s), 0, 0,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1,
        ];
    }

    /// <summary>
    /// SVG <c>feColorMatrix type="hueRotate"</c> 的原式（RGB→YIQ→旋转→YIQ→RGB 推出来的）。
    /// 交叉项 0.143 / 0.140 / −0.283 来自 YIQ 分解，<b>不能</b>从 luma 三个数泛化出来 ——
    /// 想当然地写成 <c>±Lb·sin</c> 会得到一个"看起来像色相旋转"但灰阶不守恒的矩阵。
    /// </summary>
    private static float[] Hue(double radians)
    {
        var cos = (float)Math.Cos(radians);
        var sin = (float)Math.Sin(radians);

        return [
            0.213f + cos * 0.787f - sin * 0.213f, 0.715f - cos * 0.715f - sin * 0.715f, 0.072f - cos * 0.072f + sin * 0.928f, 0, 0,
            0.213f - cos * 0.213f + sin * 0.143f, 0.715f + cos * 0.285f + sin * 0.140f, 0.072f - cos * 0.072f - sin * 0.283f, 0, 0,
            0.213f - cos * 0.213f - sin * 0.787f, 0.715f - cos * 0.715f + sin * 0.715f, 0.072f + cos * 0.928f + sin * 0.072f, 0, 0,
            0, 0, 0, 1, 0,
            0, 0, 0, 0, 1,
        ];
    }

    private static float[] Multiply(float[] a, float[] b)
    {
        var r = new float[25];
        for (var row = 0; row < 5; row++)
        {
            for (var col = 0; col < 5; col++)
            {
                var sum = 0f;
                for (var k = 0; k < 5; k++)
                {
                    sum += a[row * 5 + k] * b[k * 5 + col];
                }

                r[row * 5 + col] = sum;
            }
        }

        return r;
    }

    private static float[] To4x5(float[] m)
    {
        var r = new float[20];
        for (var row = 0; row < 4; row++)
        {
            Array.Copy(m, row * 5, r, row * 5, 5);
        }

        return r;
    }
}
