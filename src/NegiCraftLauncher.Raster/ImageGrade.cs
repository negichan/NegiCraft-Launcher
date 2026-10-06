namespace NegiCraftLauncher.Raster;

/// <summary>
/// 给预乘 BGRA 的位图套 <see cref="ColorMatrix"/>（壁纸调色）。
///
/// <para><b>预乘的正确姿势</b>（<c>AvatarComposer.cs</c> 在 alpha 上栽过一次，别再栽第二次）：
/// 色彩矩阵定义在<b>非预乘</b>的分量上，所以顺序必须是
/// 摊回直通 → 套矩阵 → 再乘 alpha → 写回。少了第一步，半透明像素会被系统性算暗；
/// 少了第三步，输出就不再是 Pbgra32，上屏会发白。</para>
///
/// <para>壁纸几乎都是不透明照片，所以 <c>a == 255</c> 走的是跳过两次乘除的快路径；
/// 这个分支几乎永远命中，分支预测成本可以忽略。</para>
///
/// <para><b>空域</b>：全程在 8-bit gamma 空间算，不做线性化。视频壁纸不吃调色（只共享取景），
/// 所以这条管线只有这一个实现，不存在"两边算出两种颜色"的问题。</para>
/// </summary>
public static class ImageGrade
{
    /// <summary>
    /// 把 <paramref name="source"/> 调色后写进 <paramref name="destination"/>。
    /// 两个数组可以是同一个（就地调色，逐元素读写同下标，安全）。
    /// </summary>
    public static void Grade(uint[] source, uint[] destination, float[] m, int from = 0, int length = -1)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(m);

        if (m.Length != 20)
        {
            throw new ArgumentException("色彩矩阵必须是 4×5 = 20 个 float。", nameof(m));
        }

        if (length < 0) length = source.Length - from;

        // 行主序，行向量约定 out = in · M，in = (r,g,b,a,1)。第 5 列已含 ×255。
        var r0 = m[0]; var r1 = m[1]; var r2 = m[2]; var r3 = m[3]; var r4 = m[4];
        var g0 = m[5]; var g1 = m[6]; var g2 = m[7]; var g3 = m[8]; var g4 = m[9];
        var b0 = m[10]; var b1 = m[11]; var b2 = m[12]; var b3 = m[13]; var b4 = m[14];

        var end = from + length;
        for (var i = from; i < end; i++)
        {
            var p = source[i];
            var a = (int)(p >> 24);
            if (a == 0)
            {
                destination[i] = 0;
                continue;
            }

            var r = (int)((p >> 16) & 0xFF);
            var g = (int)((p >> 8) & 0xFF);
            var b = (int)(p & 0xFF);

            if (a != 255)
            {
                // 摊回直通也要四舍五入：写成 r*255/a 的整除会让单位矩阵不再"原样进出"
                // （128 半透明像素会掉 1）。
                r = (r * 255 + a / 2) / a;
                g = (g * 255 + a / 2) / a;
                b = (b * 255 + a / 2) / a;
            }

            var nr = RoundClamp(r0 * r + r1 * g + r2 * b + r3 * a + r4);
            var ng = RoundClamp(g0 * r + g1 * g + g2 * b + g3 * a + g4);
            var nb = RoundClamp(b0 * r + b1 * g + b2 * b + b3 * a + b4);

            if (a != 255)
            {
                nr = (nr * a + 127) / 255;
                ng = (ng * a + 127) / 255;
                nb = (nb * a + 127) / 255;
            }

            destination[i] = ((uint)a << 24) | ((uint)nr << 16) | ((uint)ng << 8) | (uint)nb;
        }
    }

    private static int RoundClamp(float v) => v <= 0 ? 0 : v >= 255 ? 255 : (int)(v + 0.5f);
}
