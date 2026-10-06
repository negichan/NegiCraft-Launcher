namespace NegiCraftLauncher.Raster;

/// <summary>
/// 页面上的一块矩形，DIP。两端各自的 <c>Rect</c> 类型不通，摆位这类几何只能吃裸矩形。
/// </summary>
public readonly record struct PageBox(double X, double Y, double Width, double Height)
{
    public readonly double Right => X + Width;
    public readonly double Bottom => Y + Height;
}

/// <summary>
/// 首页那个 3D 皮肤小人的摆位数学，两端共用一份 ⇒ 同一个落点在 WPF 与 Avalonia 上
/// 停在同一个像素上（<see cref="BackgroundFrame"/> 为同样的理由存在）。
/// </summary>
public static class HomeStageLayout
{
    /// <summary>
    /// 把"想要的舞台落点"夹成合法落点：身体不许出首页，也不许压到
    /// <paramref name="keepOut"/> 里任何一块上。
    ///
    /// <para><b>判的是 <paramref name="footprint"/>（身体在舞台里占的那一块），不是整块舞台</b>：
    /// 舞台 160×250，而名牌顶到阴影底只有 163 高、横向就是渲染视口那 110 —— 脚下白留 81 DIP。
    /// 拿整块舞台去夹，小人会在半空停住，那块看不见的边还会替它吃掉底下按钮的点击。</para>
    ///
    /// <para>障碍是<b>逐块沿压得最浅的那一轴推开</b>：贴着停靠卡横着拖就是顺着边滑，
    /// 既不会一头扎进去，也不会钉死在原地动不了。</para>
    /// </summary>
    /// <param name="wantX">想要的舞台左上角 X（首页坐标）。</param>
    /// <param name="wantY">想要的舞台左上角 Y。</param>
    /// <param name="pageW">首页可用区宽度。</param>
    /// <param name="pageH">首页可用区高度。</param>
    /// <param name="footprint">身体占位矩形，<b>以舞台左上角为原点</b>。</param>
    /// <param name="keepOut">不许压到的块，以首页左上角为原点；零面积的（还没排版）当不存在。</param>
    public static (double X, double Y) Clamp(
        double wantX,
        double wantY,
        double pageW,
        double pageH,
        PageBox footprint,
        params PageBox[] keepOut)
    {
        var x = wantX;
        var y = wantY;

        ClampToPage(ref x, ref y, pageW, pageH, footprint);

        foreach (var k in keepOut)
        {
            if (k.Width <= 0 || k.Height <= 0) continue;

            var left = x + footprint.X;
            var top = y + footprint.Y;
            var right = left + footprint.Width;
            var bottom = top + footprint.Height;

            if (right <= k.X || left >= k.Right || bottom <= k.Y || top >= k.Bottom) continue;

            // 四边各算一次"要挪多少才出来"，挑最小的那个方向退 —— 就是离障碍最近的那条路。
            var pushLeft = right - k.X;
            var pushRight = k.Right - left;
            var pushUp = bottom - k.Y;
            var pushDown = k.Bottom - top;

            if (pushLeft <= pushRight && pushLeft <= pushUp && pushLeft <= pushDown) x -= pushLeft;
            else if (pushRight <= pushUp && pushRight <= pushDown) x += pushRight;
            else if (pushUp <= pushDown) y -= pushUp;
            else y += pushDown;
        }

        // 推开可能把它推出首页，最后再夹一次。
        ClampToPage(ref x, ref y, pageW, pageH, footprint);
        return (x, y);
    }

    /// <summary>
    /// 夹的是身体而不是舞台，所以舞台那 25 DIP 的空白侧边可以安心探出页边 ——
    /// 那里本来就没有像素。页面比身体还小时把两个边界收成同一个点，不让 Clamp 的
    /// min&gt;max 反过来把小人甩出画面。
    /// </summary>
    private static void ClampToPage(ref double x, ref double y, double pageW, double pageH, PageBox f)
    {
        x = Math.Clamp(x, -f.X, Math.Max(-f.X, pageW - f.X - f.Width));
        y = Math.Clamp(y, -f.Y, Math.Max(-f.Y, pageH - f.Y - f.Height));
    }
}
