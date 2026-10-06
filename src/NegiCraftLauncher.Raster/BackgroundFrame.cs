namespace NegiCraftLauncher.Raster;

/// <summary>
/// 壁纸取景：在 cover（等价 <c>UniformToFill</c>）的基础上再平移 + 缩放，
/// 算成一个"以元素左上角为原点"的仿射矩阵。
/// </summary>
/// <param name="Scale">缩放倍率（1 = 不额外放大）。</param>
/// <param name="OffsetX">X 平移，元素本地坐标像素。</param>
/// <param name="OffsetY">Y 平移，元素本地坐标像素。</param>
/// <param name="PanStepX">横向 1% 平移量等于多少屏幕像素 —— 拖动时用 <c>dx / PanStepX</c> 换算。</param>
/// <param name="PanStepY">纵向同上。</param>
public readonly record struct FrameTransform(
    double Scale,
    double OffsetX,
    double OffsetY,
    double PanStepX,
    double PanStepY)
{
    /// <summary>是否就是今天的"居中裁切铺满"，用来决定要不要给元素挂变换。</summary>
    public bool IsCoverOnly => Scale == 1 && OffsetX == 0 && OffsetY == 0;
}

/// <summary>视频那条路的取景框：几何不在元素上，而在共享 <c>DrawingBrush</c> 的绝对 Viewbox 上。</summary>
public readonly record struct VideoViewbox(double X, double Y, double Width, double Height);

/// <summary>
/// 弹层里那块"这张图大概盖住我们多少"小预览的几何，全部以 chip 自己的像素为单位。
/// 图片矩形按 contain 放进 chip，视口矩形按<b>同一比例</b>画在它里面。
/// </summary>
public readonly record struct CoverageBoxes(
    double ImageX, double ImageY, double ImageW, double ImageH,
    double ViewX, double ViewY, double ViewW, double ViewH);

/// <summary>
/// 取景数学只写这一份，两端共用 ⇒ 同一组参数在 WPF 与 Avalonia 上落在同一个像素上。
///
/// <para><b>pan 的单位是"可平移范围的百分比"</b>（不是像素、也不是窗口尺寸）：图片铺满后
/// 比元素大出来的那部分就是总余量，±100 恰好看到对侧边。于是换窗口大小、换缩放都自洽，
/// 而且<b>钳制是免费的</b> —— 参数域本身就限定在 [-100,100]，不可能把图片拖出露边的位置。</para>
/// </summary>
public static class BackgroundFrame
{
    /// <summary>
    /// 缩放下限。<b>100% 仍然是"cover 铺满裁切"</b>，但允许往回缩到 40% ——
    /// 那时图比窗口小，露出来的地方不补图、直接是主题底色（画刷 <c>TileMode=None</c>
    /// 本来就不画 Viewbox 超出源图的部分，壳的 #09090b 自然透出来）。
    /// </summary>
    public const double MinZoom = 40;
    public const double MaxZoom = 300;

    public static double ClampZoom(double zoom) => double.IsFinite(zoom) ? Math.Clamp(zoom, MinZoom, MaxZoom) : MinZoom;

    /// <summary>cover 比例：图片铺满元素所需的最小放大（就是 UniformToFill 干的事）。</summary>
    public static double CoverScale(double shellW, double shellH, double natW, double natH)
    {
        if (shellW <= 0 || shellH <= 0 || natW <= 0 || natH <= 0) return 1;
        return Math.Max(shellW / natW, shellH / natH);
    }

    public static FrameTransform Compute(
        double shellW, double shellH, double natW, double natH,
        double panX, double panY, double zoom)
    {
        var z = ClampZoom(zoom) / 100;
        var c = CoverScale(shellW, shellH, natW, natH);

        // 余量取<b>绝对值</b>：图比窗口大是能裁的范围，图比窗口小是能滑的范围。
        // 两种情况下"拖 100% 走到贴边"的语义一致 ⇒ 缩放缩到 cover 以下也还能挪图。
        var slackX = Math.Abs(natW * c * z - shellW);
        var slackY = Math.Abs(natH * c * z - shellH);

        // p' = (p − centre)·z + centre + t ⇒ 一个矩阵搞定"绕中心缩放 + 平移"。
        // WPF 与 Avalonia 的 Matrix 构造签名都是 (m11,m12,m21,m22,offX,offY) 且同为行向量约定，
        // 所以这里只给数，不要在任何一端用 RenderTransformOrigin / TransformGroup —— 那两样
        // 两端的枢轴与复合顺序语义不同，会差半像素。
        return new FrameTransform(
            z,
            shellW * (1 - z) / 2 + panX / 100 * slackX / 2,
            shellH * (1 - z) / 2 + panY / 100 * slackY / 2,
            slackX / 2 / 100,
            slackY / 2 / 100);
    }

    /// <summary>
    /// 视频的取景框（喂给 <c>DrawingBrush.Viewbox</c>，配 <c>Stretch=Fill</c> 与绝对单位）。
    /// 屏幕像素 / 内容单位 = <c>c·z</c>，所以屏幕上的平移在内容里要往<b>反</b>方向走 <c>t/(c·z)</c>。
    /// 取景框保持与元素同宽高比 ⇒ 用 <c>Fill</c> 也不会把画面拉变形。
    /// </summary>
    /// <summary>
    /// 视频的取景框（喂给 <c>DrawingBrush.Viewbox</c>，配 <c>Stretch=Fill</c> 与绝对单位）。
    /// 屏幕像素 / 内容单位 = <c>c·z</c>，所以屏幕上的平移在内容里要往<b>反</b>方向走 <c>t/(c·z)</c>。
    /// 取景框保持与元素同宽高比 ⇒ 用 <c>Fill</c> 也不会把画面拉变形。
    ///
    /// <para>缩放小于 cover 时取景框会<b>超出源图</b>：多出来的那圈不画（<c>TileMode=None</c>），
    /// 壳的主题底色透出来，这就是"其他地方按主题色填充"。平移量取绝对值，所以那时候
    /// 图还能在窗口里滑到贴边，而不是被钉死在中间。</para>
    /// </summary>
    public static VideoViewbox ComputeVideoViewbox(
        double shellW, double shellH, double natW, double natH,
        double panX, double panY, double zoom)
    {
        var z = ClampZoom(zoom) / 100;
        var c = CoverScale(shellW, shellH, natW, natH) * z;
        var w = shellW / c;
        var h = shellH / c;

        return new VideoViewbox(
            (natW - w) / 2 - panX / 100 * Math.Abs(natW - w) / 2,
            (natH - h) / 2 - panY / 100 * Math.Abs(natH - h) / 2,
            w,
            h);
    }

    /// <summary>
    /// 弹层里那块覆盖预览的几何。
    ///
    /// <para><b>整个算在"原图单位"里</b>，不碰屏幕坐标：取景真正生效的地方是
    /// <see cref="ComputeVideoViewbox"/> 交出去的那个 Viewbox（图片和视频都走它），
    /// 而 <see cref="Compute"/> 的偏移是"cover 之后再加"的相对量 —— 拿它画 chip 会漏掉
    /// cover 自己那圈裁切（1180 的壳里 1200 的图，左右各 10px 就看不见）。</para>
    /// </summary>
    public static CoverageBoxes ComputeCoverage(
        double chipW, double chipH, double shellW, double shellH,
        double natW, double natH, double panX, double panY, double zoom,
        double inset = 4)
    {
        if (chipW <= 0 || chipH <= 0 || natW <= 0 || natH <= 0)
        {
            return new CoverageBoxes(0, 0, 0, 0, 0, 0, 0, 0);
        }

        var vb = ComputeVideoViewbox(shellW, shellH, natW, natH, panX, panY, zoom);

        // contain：整张图缩进 chip，视口按同一比例跟着缩。
        var k = Math.Min((chipW - inset * 2) / natW, (chipH - inset * 2) / natH);
        var iw = natW * k;
        var ih = natH * k;
        var ix = (chipW - iw) / 2;
        var iy = (chipH - ih) / 2;

        return new CoverageBoxes(ix, iy, iw, ih, ix + vb.X * k, iy + vb.Y * k, vb.Width * k, vb.Height * k);
    }
}
