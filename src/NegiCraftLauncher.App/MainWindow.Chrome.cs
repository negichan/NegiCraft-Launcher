using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;
using Forms = System.Windows.Forms;

namespace NegiCraftLauncher.App;

/// <summary>
/// 主窗口的「外框」：无边框窗口的缩放命中、拖动、最小化/关闭、右上角按钮的取色，
/// 以及贴着喇叭那条竖排音量浮层。
///
/// <para>和 <c>MainWindow.Shell.cs</c> 的区别：那边管的是窗口<b>之外</b>（托盘、桌宠），
/// 这边管的是窗口<b>自己那圈边框</b>。从 <c>MainWindow.xaml.cs</c> 原样搬来。</para>
/// </summary>
public partial class MainWindow
{
    // ==========================================================
    // 窗口按钮图标色（按底下的背景自动变）
    // ==========================================================

    /// <summary>
    /// 静止色只有两档：暗底 <c>#a1a1aa</c>（原来就是这个）、亮底 <c>#18181b</c>。
    /// 换的是整套 <c>Style</c> 而不是 <c>Foreground</c>，理由见 XAML 里
    /// <c>WinCtrlLightStyle</c> 的注释（本地值会压死样式触发器）。
    ///
    /// <para>阈值取 WCAG 那个交叉点：相对亮度 <c>0.179</c> 处，深色和浅色前景的对比度相等
    /// （解 <c>1.05/(L+0.05) = (L+0.05)/0.05</c>）。所以判据是 0.179 而不是 0.5 ——
    /// 相对亮度是线性化的，中灰（sRGB 128）只有 0.216，拿 0.5 当界会把一大片"其实已经偏亮"
    /// 的底色判成暗底。</para>
    ///
    /// <para>上下各留一段滞回（<c>0.22</c> / <c>0.15</c>）：视频里一闪而过的亮帧
    /// 不至于让图标抖个不停。</para>
    /// </summary>
    private void InvalidateWindowButtonTone()
    {
        _bgToneDirty = true;
        QueueWindowButtonTone();
    }

    private bool CanSampleTone => IsLoaded && IsVisible && LauncherScene.IsVisible &&
        WindowState != WindowState.Minimized && !_dragPerfHold && IsActive;

    private void SyncToneSampling()
    {
        if (_bgToneTimer is null) return;
        if (CanSampleTone && _vm?.IsVideoBackground == true)
            _bgToneTimer.Start();
        else
            _bgToneTimer.Stop();
        QueueWindowButtonTone();
    }

    private void QueueWindowButtonTone()
    {
        if (_bgToneQueued || !_bgToneDirty || !CanSampleTone) return;
        _bgToneQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _bgToneQueued = false;
            if (_bgToneDirty && CanSampleTone) UpdateWindowButtonTone();
        }, DispatcherPriority.Background);
    }

    private void UpdateWindowButtonTone()
    {
        // 窗口收进了托盘、或者还没量出尺寸 ⇒ 一律按深色算。背景现在每页都在，
        // 所以这里不再问"在不在首页"。
        if (!IsVisible || BgRoot.ActualWidth < 1)
        {
            SetWindowButtonTone(false);
            return;
        }

        // 采样区：喇叭 + 最小化 + 关闭 一共 104px 宽（32*3 + 4*2），左右各留一点余量，
        // 纵向取按钮条上下各多 6px。坐标系就是 BgRoot 自己的，它铺满整个窗口壳。
        var w = BgRoot.ActualWidth;
        var region = new Rect(Math.Max(0, w - 126), 6, 120, 44);

        var lum = SampleLuminance(BgRoot, region);
        if (lum is not double l)
        {
            return;
        }

        _lastLum = l;
        _bgToneDirty = false;

        if (_winCtrlLight)
        {
            if (l < 0.15) SetWindowButtonTone(false);
        }
        else if (l > 0.22)
        {
            SetWindowButtonTone(true);
        }
    }

    private void SetWindowButtonTone(bool light)
    {
        if (_winCtrlLight == light) return;
        _winCtrlLight = light;

        var normal = (Style)FindResource(light ? "WinCtrlLightStyle" : "WinCtrlStyle");
        var close = (Style)FindResource(light ? "WinCtrlCloseLightStyle" : "WinCtrlCloseStyle");

        MinimizeButton.Style = normal;
        VideoSoundButton.Style = normal;
        CloseButton.Style = close;
    }

    /// <summary>
    /// 采一块背景区域的平均相对亮度（WCAG 那套先线性化再加权）。
    ///
    /// <para>做法是拿 <see cref="VisualBrush"/> 的 <c>Viewbox</c> 把 <paramref name="source"/>
    /// 裁到 <paramref name="region"/>，再画进一张 24x8 的小位图 —— 读的是<b>已经合成好的</b>画面，
    /// 所以 <c>BlurEffect</c>、亮度叠加层、<c>.shade</c> 渐变全都算在内，
    /// 不用自己把合成公式再复刻一遍（复刻一遍迟早会跟 XAML 走岔）。</para>
    ///
    /// <para>只读 192 个像素；真正花时间的是让 WPF 把这块背景渲一遍，
    /// 所以调用方是 600ms 一次、不是每帧。</para>
    /// </summary>
    private static double? SampleLuminance(FrameworkElement source, Rect region)
    {
        const int W = 24;
        const int H = 8;

        if (region.Width < 1 || region.Height < 1) return null;
        if (source.ActualWidth < 1 || source.ActualHeight < 1) return null;

        try
        {
            var brush = new VisualBrush(source)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = region,
                Stretch = Stretch.Fill,
            };

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(brush, null, new Rect(0, 0, W, H));
            }

            var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var pixels = new byte[W * H * 4];
            rtb.CopyPixels(pixels, W * 4, 0);

            double sum = 0;
            for (var i = 0; i < pixels.Length; i += 4)
            {
                // Pbgra32：背景是实心的，alpha 恒为 255，所以不用反预乘。
                sum += RelativeLuminance(pixels[i + 2], pixels[i + 1], pixels[i]);
            }

            return sum / (W * H);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Lin(byte c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    /// <summary>调试桥用：图标色此刻是亮底档还是暗底档，以及最近一次采到的亮度。</summary>
    public string WindowToneDebug =>
        $"light={_winCtrlLight} lum={(_lastLum is double l ? l.ToString("0.###") : "n/a")}";

    /// <summary>右上角喇叭：点一下出声 ↔ 静音。静音就是音量归 0，再点回去上一次那个音量。</summary>
    private void OnVideoSoundClick(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        _vm.VideoBackgroundSound = !_vm.VideoBackgroundSound;
    }

    // ==========================================================
    // 音量浮层的开合
    // ==========================================================

    // 开合的时序（悬停犹豫期、收合轮询、暂停）全在 Controls/HoverReveal.cs，
    // 这里只剩"开之前把横向位置算准"这一件浮层特有的事。

    /// <summary>
    /// 把浮层横向摆到"喇叭正下方"。
    ///
    /// <para><b>为什么不在 XAML 里写死右边距</b>：浮层靠 <c>HorizontalAlignment="Right"</c> +
    /// 右边距定位，而喇叭在右上角那一排里的位置取决于它右边有几个按钮、间距多少 —— 写死一个数字，
    /// 改那一排任何一处就偏了。所以每次开之前按"喇叭中心 − 浮层宽/2"现算。</para>
    ///
    /// <para><b>两个数必须在同一个坐标系里比</b>：浮层的 <c>Margin</c> 是相对它那个父容器算的，
    /// 而父容器在 <c>Shell</c> 那 1px 描边<b>里面</b>。之前拿窗口的 <c>ActualWidth</c> 去除，
    /// 正好差那 1px 的 inset，箭头永远对不齐喇叭 —— 而且浮层宽度已经写死（见 XAML 那条注释），
    /// 不会再被百分比文字的宽度带着飘。</para>
    /// </summary>
    private void RepositionVolumePanel()
    {
        if (VisualTreeHelper.GetParent(VolumePanel) is not FrameworkElement host)
        {
            _volumeReposition = "父容器不是 FrameworkElement";
            return;
        }

        // 浮层在可视树里（只是 Opacity=0），正常早就量过了；保险起见补一次布局。
        var w = VolumePanel.ActualWidth;
        if (w < 1)
        {
            UpdateLayout();
            w = VolumePanel.ActualWidth;
        }
        if (w < 1)
        {
            _volumeReposition = "浮层还没量出宽度";
            return;
        }

        var center = VideoSoundButton.TransformToVisual(host)
            .Transform(new Point(VideoSoundButton.ActualWidth / 2, 0)).X;

        var right = Math.Max(0, host.ActualWidth - center - w / 2);
        VolumePanel.Margin = new Thickness(0, VolumePanel.Margin.Top, right, 0);
        _volumeReposition = $"host={host.ActualWidth:0.#} 喇叭中心={center:0.#} 宽={w:0.#} → 右边距={right:0.#}";
    }

    /// <summary>最近一次横向定位的算式（<c>state</c> 的 speaker= 段里能看到）。没跑过就是"还没算过"。</summary>
    private string _volumeReposition = "还没算过";

    /// <summary>
    /// 音量条"点哪儿就跳到哪儿"。
    ///
    /// <para><b>为什么要自己算</b>：WPF 的 <see cref="Track"/> 上<b>没有</b> IsMoveToPointEnabled
    /// （那是 Avalonia / WinUI 的属性，写在这儿连编译都过不去），而模板里那两根 RepeatButton
    /// 又没绑 LargeChange 命令 —— 所以点轨道本来是一点反应都没有，只有那个小圆钮能拖。</para>
    ///
    /// <para><b>按在钮上必须让路</b>：Preview 那一下是从外向里传的，在这里抢先 Handle 会把钮自己的
    /// 拖拽掐死。所以先顺着可视树从 OriginalSource 往上认，落在 <see cref="Thumb"/> 里就什么都不做。</para>
    /// </summary>
    private void OnVolumeBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (SetVolumeFromPoint(e)) e.Handled = true;
    }

    /// <summary>
    /// 按着之后在条里拖，值跟着走。
    /// 故意<b>不抓鼠标</b>：一 Capture，<c>IsMouseOver</c> 就不跟指针了，那台收合看门狗会当场把浮层关掉。
    /// </summary>
    private void OnVolumeBarMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) SetVolumeFromPoint(e);
    }

    private bool SetVolumeFromPoint(MouseEventArgs e)
    {
        for (var v = e.OriginalSource as DependencyObject; v is not null && v != VideoVolumeSlider; v = VisualTreeHelper.GetParent(v))
        {
            if (v is Thumb) return false;
        }

        if (VideoVolumeSlider.Template?.FindName("PART_Track", VideoVolumeSlider) is not Track track) return false;
        var value = ValueAtY(e.GetPosition(track).Y);
        if (double.IsNaN(value)) return false;
        VideoVolumeSlider.Value = value;
        return true;
    }

    /// <summary>
    /// 轨道内某个纵向位置（<b>相对 Track、Y 向下为正</b>）对应多少音量；量还没排出来回 NaN。
    ///
    /// <para>钮中心的活动区间是「轨道长 − 钮长」，两端各让半个钮，所以两端都点得到 0 和 100。
    /// 轨道没写 IsDirectionReversed：0 在底下、往上长（见 VolumeBarStyle 那段实测说明）。</para>
    /// </summary>
    private double ValueAtY(double y)
    {
        if (VideoVolumeSlider.Template?.FindName("PART_Track", VideoVolumeSlider) is not Track track
            || track.Thumb is not { } thumb) return double.NaN;

        var travel = track.ActualHeight - thumb.ActualHeight;
        if (travel <= 0) return double.NaN;

        var ratio = Math.Clamp((track.ActualHeight - thumb.ActualHeight / 2 - y) / travel, 0.0, 1.0);
        // 程序改 Value 不过 IsSnapToTickEnabled 那道 snapping，这里自己按 TickFrequency 取整；
        // 设置里存的本来就是 int。
        return Math.Round(VideoVolumeSlider.Minimum + ratio * (VideoVolumeSlider.Maximum - VideoVolumeSlider.Minimum));
    }

    /// <summary>
    /// 点击算式的自检，出现在 <c>state</c> 的 volbar= 段。
    ///
    /// <para>判据不是"我觉得公式对"，而是拿 WPF **真实排出来的**钮中心去反查：在钮已经停着的那个 Y 上
    /// 点一下，应当原样落回当前音量（否则用户一点，音量就无声地飘一格）。顺带列出点顶/中/底会跳到几。</para>
    /// </summary>
    public string VolumeBarDebug
    {
        get
        {
            if (VideoVolumeSlider.Template?.FindName("PART_Track", VideoVolumeSlider) is not Track track
                || track.Thumb is not { } thumb) return "没有 PART_Track";

            var travel = track.ActualHeight - thumb.ActualHeight;
            if (travel <= 0) return $"track={track.ActualHeight:0.#} 钮={thumb.ActualHeight:0.#} 行程不够";

            var cy = thumb.TransformToVisual(track)
                .Transform(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2)).Y;
            var span = VideoVolumeSlider.Maximum - VideoVolumeSlider.Minimum;
            var predicted = track.ActualHeight - ((VideoVolumeSlider.Value - VideoVolumeSlider.Minimum) / span * travel + thumb.ActualHeight / 2);

            return $"track={track.ActualHeight:0.#} 钮={thumb.ActualHeight:0.#} 行程={travel:0.#} " +
                   $"v={VideoVolumeSlider.Value:0.#} 钮中心 实测={cy:0.#} 预测={predicted:0.#} 差={cy - predicted:0.##} " +
                   $"实测回读={ValueAtY(cy):0.#} 点顶/中/底={ValueAtY(0):0}/{ValueAtY(track.ActualHeight / 2):0}/{ValueAtY(track.ActualHeight):0}";
        }
    }

    // ==========================================================
    // 窗口行为
    // ==========================================================

    private bool _performanceMode;

    /// <summary>
    /// 性能模式：去掉窗口级透明，WPF 才会回到 GPU 合成。默认外观（逐像素圆角 + 外投影）
    /// 和 GPU 在 Win10 上二选一，所以这是显式开关，不是默认值。
    ///
    /// <para>做法不是只改一个属性：窗口原本四周留了 60 DIP 给手绘投影，不透明窗里这圈留白
    /// 会变成实心黑边；所以同时把壳贴到窗口边、收掉阴影层，窗口尺寸减掉两圈留白，
    /// 保持可见壳尺寸不变。圆角用 <see cref="SetWindowRgn"/> 硬裁剪（Win10 没有原生圆角），
    /// <see cref="WindowChrome"/> 保留系统缩放边框，并让 DWM 接手投影（会比原来那圈手绘阴影弱）。</para>
    /// </summary>
    private void ApplyPerformanceModeIfNeeded()
    {
        var forced = Environment.GetCommandLineArgs()
            .Contains("--performance", StringComparer.OrdinalIgnoreCase);
        if (!forced && !LauncherSettings.Load().PerformanceMode) return;

        _performanceMode = true;

        const double shadowInset = 60;

        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x09, 0x09, 0x0B));
        ShellShadowLayer.Visibility = Visibility.Collapsed;
        Shell.Margin = new Thickness(0);

        MinWidth = Math.Max(0, MinWidth - shadowInset * 2);
        MinHeight = Math.Max(0, MinHeight - shadowInset * 2);
        Width = Math.Max(MinWidth, Width - shadowInset * 2);
        Height = Math.Max(MinHeight, Height - shadowInset * 2);

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(22),
            UseAeroCaptionButtons = false,
        });

        SourceInitialized += (_, _) => ApplyRoundedWindowRegion();
        Loaded += (_, _) => ApplyRoundedWindowRegion();
        SizeChanged += (_, _) => ApplyRoundedWindowRegion();
        StateChanged += (_, _) => ApplyRoundedWindowRegion();
    }

    /// <summary>不透明窗的圆角只能靠窗口区域硬裁；坐标是物理像素，DPI 变一次就要重建。</summary>
    private void ApplyRoundedWindowRegion()
    {
        if (!_performanceMode) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        if (WindowState == WindowState.Maximized)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) return;

        var radius = (int)Math.Round(22 * dpi.DpiScaleX);
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (region == IntPtr.Zero) return;

        // SetWindowRgn 成功后区域归系统所有，不能再 DeleteObject。
        SetWindowRgn(handle, region, true);
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>
    /// 拖边缘改大小。<b>无边框 + AllowsTransparency 的窗口没有原生边框可拖</b>：壳子外面那 60px
    /// 是全透明像素，系统按 alpha 把点击直接放行给桌面，"边框"在这个窗口上并不存在。
    /// 所以在 <c>WM_NCHITTEST</c> 上把**壳子那一圈**报成 HTLEFT / HTTOPRIGHT …，交回系统跑它自己的
    /// 尺寸循环 —— 光标、Aero 贴边、MinWidth/MinHeight 全归系统管，比在 PointerMove 里自己改矩形干净。
    /// 只在壳子边缘 ±6 DIP 内接管，别处一律放行，否则会把内容区的点击吃掉。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source) source.AddHook(ResizeHitTestHook);
    }

    private IntPtr ResizeHitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmNcHitTest = 0x0084;
        if (msg != wmNcHitTest || WindowState != WindowState.Normal) return IntPtr.Zero;

        // lParam 是两个带符号 16 位（屏幕物理像素）。
        var packed = (long)lParam;
        var screen = new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
        var code = ResizeHitCode(LocalFromScreen(screen));
        if (code == 0) return IntPtr.Zero;

        handled = true;
        return new IntPtr(code);
    }

    /// <summary>屏幕物理点 → 本窗口内 DIP 坐标。无边框窗口没有非客户区，所以窗口原点就是客户区原点。</summary>
    private Point LocalFromScreen(Point screen)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not CompositionTarget target)
        {
            return new Point(-1e9, -1e9);
        }

        // 用客户区原点而不是 Left/Top：WindowChrome 下窗口原点和客户区原点可能差一圈边框。
        var origin = target.TransformFromDevice.Transform(PointToScreen(new Point(0, 0)));
        var dip = target.TransformFromDevice.Transform(screen);
        return new Point(dip.X - origin.X, dip.Y - origin.Y);
    }

    /// <summary>这个窗口内坐标落在壳子的哪条改大小把手上；0 = 不在把手上（交回系统照常处理）。</summary>
    private int ResizeHitCode(Point local)
    {        var shell = new Rect(Shell.TranslatePoint(new Point(0, 0), this), Shell.RenderSize);
        const double grip = 6;
        var left = local.X >= shell.Left - grip && local.X <= shell.Left + grip;
        var right = local.X >= shell.Right - grip && local.X <= shell.Right + grip;
        var top = local.Y >= shell.Top - grip && local.Y <= shell.Top + grip;
        var bottom = local.Y >= shell.Bottom - grip && local.Y <= shell.Bottom + grip;

        // HT 码：10 左 11 右 12 上 13 左上 14 右上 15 下 16 左下 17 右下。
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => 13,
            (_, true, true, _) => 14,
            (true, _, _, true) => 16,
            (_, true, _, true) => 17,
            (true, _, _, _) => 10,
            (_, true, _, _) => 11,
            (_, _, true, _) => 12,
            (_, _, _, true) => 15,
            _ => 0,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    /// <summary>光标的屏幕物理坐标 —— 与 WM_NCHITTEST 的 lParam 同一个口径，用来验换算。</summary>
    private static bool NativeCursor(out Point screen)
    {
        if (!GetCursorPos(out var p))
        {
            screen = default;
            return false;
        }

        screen = new Point(p.X, p.Y);
        return true;
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        BeginDragPerfHold();
        // DragMove 会一路阻塞到松开左键；拖动中抛异常会被 WPF 吞掉，所以包一层。
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            EndDragPerfHold();
        }
    }

    /// <summary>
    /// 调试桥的 <c>hit</c>：报一个点（窗口内 DIP，写成 <c>hit 220,90</c>）落在哪几颗控件的矩形里，
    /// <c>hit bounds</c> 则只报矩形。脚本不能摸鼠标，"探针该扎哪儿""这块到底是谁的"就靠这个对。
    /// 答的是**几何**包含关系、不含 z 序 —— 谁真收点击由排版顺序决定（拖拽带就在内容层之下）。
    /// </summary>
    public string HitDebug(string arg)
    {
        var probes = new (string Name, FrameworkElement? Element)[]
        {
            ("band", DragBand), ("pill", FolderPillBtn), ("loader", LoaderFilterBtn),
            ("search", SearchBox), ("folderPop", FolderPop), ("loaderPop", LoaderPop)
        };

        if (arg == "bounds")
        {
            return string.Join(" ", probes.Select(p => $"{p.Name}=[{RectText(p.Element)}]")) +
                   $" win=[{Left:0.#},{Top:0.#} {ActualWidth:0.#}x{ActualHeight:0.#} scale={VisualTreeHelper.GetDpi(this).DpiScaleX:0.##}]";
        }

        // `hit map`：拿真实光标位置对一遍"钩子里那套换算"和"WPF 自己认为的光标在哪"。
        // 跨进程 SendMessage 造出来的 WM_NCHITTEST 走不到真实 DPI 管线，只能这样验换算。
        if (arg == "map")
        {
            if (!NativeCursor(out var cur)) return "ERR no cursor";
            var local = LocalFromScreen(cur);
            var wpf = Mouse.GetPosition(this);
            return $"cur=({cur.X:0.#},{cur.Y:0.#}) local=({local.X:0.#},{local.Y:0.#}) wpf=({wpf.X:0.#},{wpf.Y:0.#}) " +
                   $"shell=[{RectText(Shell)}] ht={ResizeHitCode(local)}";
        }

        var bits = arg.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length < 2 ||
            !double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return "ERR usage: hit <x>,<y> | hit bounds";
        }

        var point = new Point(x, y);
        var on = probes.Where(p => WindowRect(p.Element).Contains(point)).Select(p => p.Name);
        return $"hit by={(on.Any() ? string.Join(",", on) : "none")} ht={ResizeHitCode(point)}";
    }

    /// <summary>元素在窗口 DIP 坐标系里的矩形。<c>TranslatePoint</c> 是带上 RenderTransform 的，
    /// 所以浮层开着时读到的是它真正停住的位置（Pop.Pin 那次平移也算在内）。</summary>
    private Rect WindowRect(FrameworkElement? element)
    {
        if (element is null) return Rect.Empty;
        var at = element.TranslatePoint(new Point(0, 0), this);
        return new Rect(at, new Size(element.ActualWidth, element.ActualHeight));
    }

    private string RectText(FrameworkElement? element)
    {
        if (element is null) return "n/a";
        var r = WindowRect(element);
        return $"{r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#}";
    }

    /// <summary>
    /// 拖动这一段的"每帧重画"按住不动：视频来一帧 ⇒ 压在它上面的两层 BlurEffect 跟着跑
    /// ⇒ 分层窗口整面重呈现。所以掐三个源头——播放器、亮度采样（它自己会用 VisualBrush
    /// 把整棵 BgRoot 再渲染一遍）、音量悬停轮询（这时鼠标被 DragMove 捕获，问不出东西）。
    ///
    /// <para>拆成两个方法是为了让调试桥能走**同一对调用**：脚本不能真去按鼠标拖窗口，
    /// 而"暂停中那一帧还在不在屏上"恰恰只有看图能判。</para>
    /// </summary>
    public void BeginDragPerfHold()
    {
        _dragPerfHold = true;
        _videoBackground.FreezeForDrag();
        _bgToneTimer.Stop();
        HoverReveal.SetSuspended(VolumePanel, true);

        // 拖动中别去更新头的朝向：窗口的可视变换在移动过程中是旧的，而光标位置是新的，
        // 两者相减得到的偏移会让头先拧到一个错方向、下一帧再纠正 —— 看着就是"闪一下朝向"。
        SkinPreview.SuspendPointerLook = true;
    }

    /// <summary>
    /// 松手恢复。刻意不在这里同步补采一次亮度：整段拖动里上屏的一直是同一帧，
    /// 图标色不可能变；而补采要用 <c>VisualBrush</c> 把整棵 <c>BgRoot</c>（含视频）
    /// 离屏再渲染一遍 —— 在松手那一刻做一次视频面的重新获取，正是"背景黑一下"的现场。
    /// 600ms 的轮询本来就会跟上，晚一拍没有任何代价。
    /// </summary>
    public void EndDragPerfHold()
    {
        _dragPerfHold = false;
        SkinPreview.SuspendPointerLook = false;
        SyncToneSampling();
        HoverReveal.SetSuspended(VolumePanel, false);

        // 恢复播放也推到本轮渲染之后：松手那一帧不该还有别的事做。
        Dispatcher.BeginInvoke(_videoBackground.ResumeAfterDrag, DispatcherPriority.Background);
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => _vm?.RequestClose();

}
