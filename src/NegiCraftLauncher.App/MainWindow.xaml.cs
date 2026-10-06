using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;
using Forms = System.Windows.Forms;

namespace NegiCraftLauncher.App;

/// <summary>
/// 主窗口。逐条对应 Avalonia 侧 <c>App/Views/MainWindow.axaml.cs</c> 的事件处理，
/// 差别只在平台 API：
/// <list type="bullet">
/// <item><c>BeginMoveDrag</c> → <see cref="Window.DragMove"/>（必须按着左键调）。</item>
/// <item><c>StorageProvider.OpenFilePickerAsync</c> → <see cref="Microsoft.Win32.OpenFileDialog"/>。</item>
/// <item><c>TrayIcon</c>（Avalonia 自带）→ WinForms 的 <see cref="Forms.NotifyIcon"/>，WPF 没有托盘 API。
///   但<b>菜单不交给 WinForms</b>：<see cref="Forms.ContextMenuStrip"/> 画的是系统原生外观，
///   与深色主题对不上。托盘只用 <c>NotifyIcon</c> 显示图标，右键自己弹 WPF 的
///   <see cref="ContextMenu"/>（<c>TrayMenu</c>），样式走主题里那套 ContextMenu / MenuItem。</item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private PetWindow? _petWindow;
    private Forms.NotifyIcon? _trayIcon;
    private ContextMenu? _trayMenu;
    private MenuItem? _petTrayMenuItem;
    private bool _isExplicitExit;

    /// <summary>
    /// 视频背景。一份 <c>MediaPlayer</c> 供主背景层和侧栏背板两处复用，
    /// 见 <see cref="Media.VideoBackgroundController"/>。
    /// </summary>
    private readonly Media.VideoBackgroundController _videoBackground = new();

    /// <summary>
    /// 右上角图标色的采样节拍。600ms 是权衡：视频会一直变，所以不能只在换背景时采一次；
    /// 但每次采样都要让 WPF 把那块背景重渲一遍，所以也不能跟着每帧走。
    /// 优先级压到 <c>Background</c>，永远不跟渲染抢。
    /// </summary>
    private readonly DispatcherTimer _bgToneTimer;

    /// <summary>图标现在是不是亮底档（深色图标）。</summary>
    private bool _winCtrlLight;

    /// <summary>最近一次采到的平均相对亮度，只在调试桥上用。</summary>
    private double? _lastLum;

    public MainWindow()
    {
        InitializeComponent();
        SetupTrayIcon();
        DataContextChanged += OnDataContextChangedHandler;

        // 自选壁纸与视频同构：一个 brush 挂两处元素，取景只改 brush（见 ApplyBackgroundFraming）。
        BgCustomImage.Fill = _customBackground;
        SidebarCustomImage.Fill = _customBackground;

        _videoBackground.Failed += OnVideoBackgroundFailed;
        // 视频的原生尺寸要等 MediaOpened 才可信，那一刻重算一次画中画（之前是 16:9 占位）。
        _videoBackground.Opened += UpdateBackgroundShot;
        IsVisibleChanged += (_, _) => UpdateVideoPlayback();

        // 音量浮层是"喇叭中心 − 浮层宽/2"算出来的，窗口一改尺寸那个中心就变了。
        // 只在鼠标进喇叭时算一次的话，改过窗口宽度后再悬停，箭头会指到别处去。
        SizeChanged += (_, _) => RepositionVolumePanel();

        // 背景调节窗跟着主窗口的可见性走：主窗口最小化或收进托盘时它不能一个人留在桌面上，
        // 还原时又得自己回来。挂在事件上而不是挂在 OnMinimizeClick / OnCloseClick 里 ——
        // "启动后隐藏启动器"那条路是 VM 直接调 Hide() 的，只盯按钮会漏。
        IsVisibleChanged += (_, _) => SyncBackgroundTuningVisibility();
        StateChanged += (_, _) => SyncBackgroundTuningVisibility();
        LocationChanged += (_, _) => FollowMainWindowIfNotPlaced();

        _bgToneTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _bgToneTimer.Tick += (_, _) => UpdateWindowButtonTone();
        _bgToneTimer.Start();

        _volumeHoverTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(VolumeHoverPollMs),
        };
        _volumeHoverTimer.Tick += (_, _) => CheckVolumeHover();

        // 首帧就要定色，不能等第一个 tick —— 否则窗口会先闪一下错色的图标。
        Loaded += (_, _) => UpdateWindowButtonTone();
    }

    /// <summary>调试桥通过它拿桌宠窗口（可以关着，所以可空）。</summary>
    public PetWindow? PetWindowInstance => _petWindow;

    /// <summary>
    /// WPF 的 <c>Border</c> 即使 <c>ClipToBounds=True</c> 也只裁矩形，圆角得自己给一条裁剪几何，
    /// 否则满幅的首页背景会在四个角上露出直角。
    /// <para>半径 <c>22</c> 与 XAML 里 <c>Shell</c> 的 <c>CornerRadius="22"</c> 一致。
    /// 这条几何裁剪本身是带抗锯齿的（45° 弧上边界像素约 44% 覆盖），窗口边缘看着毛糙
    /// 不是它的锅。</para>
    /// </summary>
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Shell.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 22, 22);

        // 取景的余量是按壳尺寸算的，尺寸一变就要重算（视频的 Viewport 也绑在这个尺寸上）。
        ApplyBackgroundFraming();
    }

    /// <summary>
    /// 侧栏磨砂背板同理：<c>ClipToBounds=True</c> 只裁矩形，圆角得自己给一条裁剪几何，
    /// 否则被 <c>BlurEffect</c> 放大的背景图会从四个圆角外漏出来（看着就是矩形 + 角上一圈糊）。
    /// 半径要和 XAML 里的 <c>CornerRadius="18"</c> 保持一致。
    /// </summary>
    private void OnSidebarBackdropSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SidebarBackdrop.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 18, 18);
        ApplyBackgroundFraming();
    }

    // ==========================================================
    // 托盘
    // ==========================================================

    private void SetupTrayIcon()
    {
        try
        {
            // 菜单是 XAML 里的 TrayMenu（主题样式），这里只把它取出来。
            _trayMenu = (ContextMenu)FindResource("TrayMenu");
            _petTrayMenuItem = _trayMenu.Items.OfType<MenuItem>().FirstOrDefault(m => "pet".Equals(m.Tag));

            var icon = Environment.ProcessPath is { Length: > 0 } exe
                ? System.Drawing.Icon.ExtractAssociatedIcon(exe)
                : null;

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "NegiCraft Launcher",
                Visible = true,
            };
            // 不设 ContextMenuStrip —— 让 WinForms 别接管右键，自己弹 WPF 菜单。
            _trayIcon.MouseUp += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) ShowTrayMenu();
            };
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) Restore();
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrayIcon] Init error: {ex.Message}");
        }
    }

    /// <summary>
    /// 在光标处弹出托盘菜单。
    ///
    /// <para>⚠️ 不能用 <see cref="PlacementMode.MousePoint"/>：WPF 用的是它自己缓存的鼠标位置，
    /// 而那份缓存只在窗口收到鼠标消息时才更新 —— 托盘右键时窗口通常不在光标底下（甚至已隐藏），
    /// 菜单会弹到上次鼠标经过窗口的位置去。这里直接问 Win32 要光标的<b>物理像素</b>位置，
    /// 再按窗口 DPI 折成 DIP 喂给 <see cref="PlacementMode.AbsolutePoint"/>。</para>
    /// </summary>
    private void ShowTrayMenu()
    {
        if (_trayMenu is null) return;

        var pos = Forms.Cursor.Position;                 // 物理屏幕像素
        var dpi = VisualTreeHelper.GetDpi(this);

        _trayMenu.PlacementTarget = this;                // 只为拿到主题里的隐式样式与焦点归属
        _trayMenu.Placement = PlacementMode.AbsolutePoint;
        _trayMenu.HorizontalOffset = pos.X / dpi.DpiScaleX;
        _trayMenu.VerticalOffset = pos.Y / dpi.DpiScaleY;
        _trayMenu.IsOpen = true;
    }

    private void OnTrayOpenLauncherClick(object sender, RoutedEventArgs e) => Restore();

    private void OnTrayTogglePetClick(object sender, RoutedEventArgs e) => TogglePetWindow();

    private void OnTrayExitClick(object sender, RoutedEventArgs e) => ExitApplication();

    public void ExitApplication()
    {
        _isExplicitExit = true;

        _petWindow?.Close();
        _petWindow = null;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        Close();
        Application.Current?.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 点关闭只是收进托盘；真正退出走 ExitApplication。
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        // 真正退出才释放：收进托盘那条路只是 Hide，窗口还要复用。
        // 不 Close 掉 MediaPlayer 会留一个解码线程不放。
        _videoBackground.Dispose();

        base.OnClosing(e);
    }

    // ==========================================================
    // 视频背景
    // ==========================================================

    /// <summary>
    /// 把 VM 里的视频路径落到画面上。VM 只存路径（它不能持有 WPF 的播放器），
    /// 播放器和画刷都在这里建，然后同一个画刷挂给两处元素。
    /// </summary>
    private void ApplyVideoBackground()
    {
        // 先定音量再换片：Load 内部会在 Open 之后按当前值再断言一次，顺序反了会白做一次。
        _videoBackground.Volume = _vm?.VideoBackgroundVolume ?? 0;
        _videoBackground.Load(_vm?.VideoBackgroundPath);

        var brush = _videoBackground.HasVideo ? _videoBackground.Brush : null;
        BgVideo.Fill = brush;
        SidebarVideo.Fill = brush;

        UpdateVideoPlayback();
    }

    // ==========================================================
    // 背景取景（平移 + 缩放）
    // ==========================================================

    /// <summary>
    /// 自选壁纸的画刷。几何全在<b>绝对</b> Viewbox 上，主层与侧栏共用这一个实例。
    /// </summary>
    private readonly ImageBrush _customBackground = new()
    {
        Stretch = Stretch.Fill,
        ViewboxUnits = BrushMappingMode.Absolute,
        ViewportUnits = BrushMappingMode.Absolute,
    };

    /// <summary>复用视图那个转换器，像素格式（Pbgra32 + 冻结）的约定就只有一处实现。</summary>
    private static readonly Converters.PixelBufferToBitmapConverter ToBitmap = new();

    /// <summary>
    /// 把取景参数落到三处：主层、侧栏背板，以及视频那条共享画刷。
    /// 图片和视频现在是同一条路 —— 几何都写在自己 brush 的<b>绝对</b> Viewbox 上，
    /// 主层与侧栏共用同一个 brush 实例 ⇒ 改一处两处同步，对齐由构造保证
    /// （两个元素的本地矩形都等于壳矩形）。
    /// </summary>
    private void ApplyBackgroundFraming()
    {
        var vm = _vm;
        if (vm is null) return;

        var shellW = Shell.ActualWidth;
        var shellH = Shell.ActualHeight;
        if (shellW <= 0 || shellH <= 0) return;   // 还没排版，等 Shell.SizeChanged 再来

        if (vm.BgCustomArt is { } art)
        {
            var box = BackgroundFrame.ComputeVideoViewbox(
                shellW, shellH, art.Width, art.Height, vm.BgPanX, vm.BgPanY, vm.BgZoom);

            _customBackground.Viewport = new Rect(0, 0, shellW, shellH);
            _customBackground.Viewbox = new Rect(box.X, box.Y, box.Width, box.Height);
        }

        _videoBackground.SetFrame(shellW, shellH, vm.BgPanX, vm.BgPanY, vm.BgZoom);
        UpdateBackgroundShot();
    }

    /// <summary>画中画那格的位图刷：整张图铺满，不带取景 —— 取景是白框负责表达的那部分。</summary>
    private readonly ImageBrush _shotBrush = new() { Stretch = Stretch.Fill };

    /// <summary>
    /// 右上角那格画中画：整张图 contain 进来，白框 = 窗口真正看得见的那一块。
    /// 两个矩形都出自共享的 <see cref="BackgroundFrame.ComputeCoverage"/>，和上屏用的是同一组数，
    /// 所以它不可能和背景画面对不上。位图直接复用主层那张已冻结的（零额外解码 / 零额外转换）。
    /// </summary>
    private void UpdateBackgroundShot()
    {
        var vm = _vm;
        if (vm is null) return;

        // 视频也画：它没有解码缓冲，所以只画几何（整张画面的矩形 + 窗口看得见的那一块）。
        var nat = PanContentSize();
        var shellW = Shell.ActualWidth;
        var shellH = Shell.ActualHeight;
        if (nat.Width <= 0 || nat.Height <= 0 || shellW <= 0 || shellH <= 0) return;

        var b = BackgroundFrame.ComputeCoverage(
            BgShot.Width, BgShot.Height, shellW, shellH,
            nat.Width, nat.Height, vm.BgPanX, vm.BgPanY, vm.BgZoom);

        PlaceInShot(BgShotImage, b.ImageX, b.ImageY, b.ImageW, b.ImageH);
        PlaceInShot(BgShotView, b.ViewX, b.ViewY, b.ViewW, b.ViewH);

        if (vm.BgCustomArt is { } art)
        {
            // 图片：直接复用主层那张已冻结的位图（零额外解码、零额外转换）。
            _shotBrush.ImageSource = _customBackground.ImageSource;
            BgShotImage.Fill = _shotBrush;
        }
        else
        {
            // 视频：拿第二支画刷，Viewbox 是整帧、Viewport 就是上面算好的那块图片矩形
            // ⇒ 画出来的边界和白框必然和描边重合。播放器还是那一个，解码只有一次。
            _videoBackground.SetPipViewport(new Rect(b.ImageX, b.ImageY, b.ImageW, b.ImageH));
            BgShotImage.Fill = _videoBackground.PipBrush;
        }

        _coverageBoxes = b;
    }

    private static void PlaceInShot(System.Windows.Shapes.Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = w;
        r.Height = h;
    }

    /// <summary>调试桥用：chip 最后一次画出来的几何（两端读同一个数，才知道是不是真的一致）。</summary>
    private CoverageBoxes _coverageBoxes;

    public string CoverageDebug
    {
        get
        {
            var b = _coverageBoxes;
            return b.ImageW <= 0
                ? "n/a"
                : $"img {b.ImageW:0.##}x{b.ImageH:0.##}@({b.ImageX:0.#},{b.ImageY:0.#}) view {b.ViewW:0.##}x{b.ViewH:0.##}@({b.ViewX:0.#},{b.ViewY:0.#})";
        }
    }

    /// <summary>
    /// 换壁纸像素。转换器只在这里用一次：把 <c>PixelBuffer</c> 变成冻结位图挂到 brush 上，
    /// 于是主层与侧栏共用同一张位图（以前是三处各自 <c>PathToBitmap</c>，一张图解三遍）。
    /// </summary>
    private void ApplyCustomBackgroundImage()
    {
        _customBackground.ImageSource = _vm?.BgCustomArt is { } art
            ? (ImageSource?)ToBitmap.Convert(art, typeof(ImageSource), null, CultureInfo.CurrentCulture)
            : null;
    }

    /// <summary>调试桥用：视频播放器此刻真实的音频状态。</summary>
    public string VideoBackgroundDebug => _videoBackground.DebugState;

    /// <summary>
    /// 调试桥用：背景那条布局链各自的实际尺寸。取景余量是按"图片宽度 − 元素宽度"算的，
    /// 链上任何一层比壳宽都会把余量算错（差 20px 宽就是 5px 平移量，光看截图量不出来）。
    /// </summary>
    public string ShellSize => FormatSize(Shell);

    public string BgRootSize => FormatSize(BgRoot);

    public string BgLayerSize => FormatSize(BgLayer);

    public string BgImageSize => $"{FormatSize(BgCustomImage)} / side {FormatSize(SidebarCustomImage)}";

    private static string FormatSize(FrameworkElement e) => $"{e.ActualWidth:0.#}x{e.ActualHeight:0.#}";
    /// <summary>调试桥用：右上角喇叭按钮与音量浮层此刻的可见性 / 悬停状态 / 横向对齐。</summary>
    public string SpeakerDebug
    {
        get
        {
            var w = VolumePanel.ActualWidth;

            // ⚠️ 两个中心必须在**同一个坐标系**里比，而且要比的那件事得和 RepositionVolumePanel
            //   用的是同一套：浮层的 Margin 是相对它的父容器（Shell 里那层 Grid）的，所以这里也
            //   换算到父容器。之前这里用窗口的 ActualWidth 去减一个父容器口径的右边距，读出来的
            //   "偏 60px" 是假的 —— 浮层其实早就对齐了，是这条诊断在骗我。
            var host = VisualTreeHelper.GetParent(VolumePanel) as FrameworkElement;
            var hostW = host?.ActualWidth ?? double.NaN;
            var panelCx = host is null ? double.NaN : hostW - VolumePanel.Margin.Right - w / 2;
            var btnCx = VideoSoundButton.ActualWidth > 0 && host is not null
                ? VideoSoundButton.TransformToVisual(host)
                    .Transform(new Point(VideoSoundButton.ActualWidth / 2, 0)).X
                : double.NaN;

            return $"visible={VideoSoundButton.IsVisible} pop={_vm?.IsVolumePopOpen} " +
                   $"btnHover={VideoSoundButton.IsMouseOver} panelHover={VolumePanel.IsMouseOver} " +
                   $"panel={w:0.#}x{VolumePanel.ActualHeight:0.#} " +
                   $"cx={panelCx:0.#}/{btnCx:0.#} 差={panelCx - btnCx:0.#} slider={VideoVolumeSlider.Value:0.#} " +
                   $"算式={_volumeReposition}";
        }
    }

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
    private void UpdateWindowButtonTone()
    {
        // 不在首页（背景不可见）、窗口收进了托盘、或者还没量出尺寸 ⇒ 一律按深色算。
        if (_vm?.IsOnHome != true || !IsVisible || BgRoot.ActualWidth < 1)
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

    /// <summary>
    /// 浮层的收合看门狗。
    ///
    /// <para><b>为什么不能只靠 MouseLeave</b>：浮层挂在喇叭正下方，指针从按钮往下走的瞬间
    /// 会先离开按钮、再落进浮层，中间那一小段两边都不算"悬停"。只认事件的话浮层会在半路被关掉，
    /// 用户看到的就是"鼠标放不过去"。所以这里改成轮询：每次 tick 直接问两个元素
    /// <c>IsMouseOver</c>，只要还在任意一个里面就继续等。</para>
    ///
    /// <para>浮层那块死区靠 XAML 兜掉了（上边距贴住按钮下沿 + <c>Background="Transparent"</c>
    /// 把整块矩形都变成可命中）。开合动画是 <c>VolumePopStyle</c> 自己的那套，全程可命中，
    /// 所以这里不需要像 <c>Pop</c> 那样再配一段"动画飞行期间别判"的宽限期。</para>
    /// </summary>
    private readonly DispatcherTimer _volumeHoverTimer;

    private const int VolumeHoverPollMs = 180;

    private void OnVideoSoundMouseEnter(object sender, MouseEventArgs e) => ShowVolumePanel();

    private void OnVolumePanelMouseEnter(object sender, MouseEventArgs e) => ShowVolumePanel();

    private void ShowVolumePanel()
    {
        if (_vm is null) return;
        RepositionVolumePanel();
        _vm.IsVolumePopOpen = true;
        _volumeHoverTimer.Start();
    }

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

    private void CheckVolumeHover()
    {
        if (_vm is null || !_vm.IsVolumePopOpen)
        {
            _volumeHoverTimer.Stop();
            return;
        }

        if (VideoSoundButton.IsMouseOver || VolumePanel.IsMouseOver) return;

        _volumeHoverTimer.Stop();
        _vm.IsVolumePopOpen = false;
    }

    /// <summary>
    /// 播放器该不该在跑。
    ///
    /// <para><b>声音开着就不能因为切页而停</b>：背景画面只在首页露出，但音频是一整条 ——
    /// 之前一律按 <c>IsOnHome</c> 暂停，切到设置页音乐就断了。所以这里分开两件事：
    /// 画面要不要画，和播放器要不要跑。</para>
    ///
    /// <para>代价是"不在首页 + 声音开着"时解码器继续跑（不是白烧：那正是声音的来源）。
    /// 窗口收进托盘时仍然暂停 —— 那时候没人看也没人听。</para>
    /// </summary>
    private void UpdateVideoPlayback()
    {
        // 在首页要画面，开着声音要音频；两者都没有才值得暂停。
        var wanted = _vm is { IsOnHome: true } || _vm is { VideoBackgroundSound: true };
        _videoBackground.SetActive(IsVisible && wanted);
    }

    private void OnVideoBackgroundFailed(string message) =>
        _vm?.ReportVideoBackgroundFailure(message);

    /// <summary>
    /// 换 DataContext 时把上一份 VM 的事件全摘掉再挂新的。
    /// WPF 没有 Avalonia 的 <c>OnDataContextChanged</c> 可重写，只能订阅事件。
    /// </summary>
    private void OnDataContextChangedHandler(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.HideWindowRequested -= Hide;
            _vm.ShowWindowRequested -= Restore;
            _vm.OpenPetRequested -= OnOpenPetRequested;
            _vm.RecallPetRequested -= ClosePetWindow;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ThemeChanged -= OnThemeChanged;
            _vm.BackgroundFramingChanged -= ApplyBackgroundFraming;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.HideWindowRequested += Hide;
            _vm.ShowWindowRequested += Restore;
            _vm.OpenPetRequested += OnOpenPetRequested;
            _vm.RecallPetRequested += ClosePetWindow;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.ThemeChanged += OnThemeChanged;

            // 取景只改变换、不碰像素，所以单独一条事件：拖动平移时一次调色都不该发生。
            _vm.BackgroundFramingChanged += ApplyBackgroundFraming;

            // 设置里存着视频壁纸的话，DataContext 一到就该把它挂上。
            ApplyVideoBackground();

            // 解码可能在窗口订阅之前就完成了（VM 是先建好再挂 DataContext 的），
            // 所以这里主动取一次，不能只等 PropertyChanged。
            ApplyCustomBackgroundImage();
            ApplyBackgroundFraming();
        }

        OnDebugSectionAttach(_vm);
    }

    /// <summary>
    /// 设置页里那块 <c>Debug</c> 分区的挂载点（导航项 + 卡片）。
    /// <para><b>刻意做成 partial void</b>：实现在 <c>MainWindow.Debug.cs</c>，整个文件包在
    /// <c>#if DEBUG</c> 里。Release 构建下那个文件编译为空，编译器会把这条声明和所有调用
    /// 一并消掉 —— 于是"Debug 分区不参与发版编译"这件事由编译器保证，而不是靠运行期隐藏。</para>
    /// <para>XAML 没有 <c>#if</c>，所以这块 UI 只能代码建（见 <c>MainWindow.Debug.cs</c>）。</para>
    /// </summary>
    partial void OnDebugSectionAttach(MainWindowViewModel? vm);

    // ==========================================================
    // 桌宠（P6 落地）
    // ==========================================================

    public void TogglePetWindow()
    {
        if (_petWindow is { IsVisible: true })
        {
            ClosePetWindow();
        }
        else
        {
            OpenPetWindow();
        }
    }

    public void OpenPetWindow()
    {
        if (_petWindow is { IsVisible: true })
        {
            _petWindow.Activate();
            UpdateTrayMenu();
            return;
        }

        var currentName = _vm?.EffectivePetName ?? "pingplus";
        _petWindow = new PetWindow(currentName, _vm is null ? null : new Services.PetHostAdapter(_vm, this));
        _petWindow.PlaceAtDefaultCorner();

        _petWindow.Closed += (_, _) =>
        {
            _petWindow = null;
            if (_vm != null) _vm.IsPetActive = false;
            UpdateTrayMenu();
        };
        _petWindow.Show();
        if (_vm != null)
        {
            _vm.IsPetActive = true;
        }
        UpdateTrayMenu();
    }

    public void ClosePetWindow()
    {
        _petWindow?.Close();
        _petWindow = null;
        if (_vm != null)
        {
            _vm.IsPetActive = false;
        }
        UpdateTrayMenu();
    }

    private void UpdateTrayMenu()
    {
        if (_petTrayMenuItem != null)
        {
            _petTrayMenuItem.Header = _petWindow is { IsVisible: true } ? "收起桌宠" : "桌面宠物";
        }
    }

    /// <summary>
    /// 调试用：程序化弹出托盘右键菜单。Avalonia 那边得靠反射调 <c>TrayIcon._impl.OnRightClicked</c>，
    /// WPF 侧右键本来就是自己弹 <see cref="ContextMenu"/>，直接调 <see cref="ShowTrayMenu"/> 即可。
    /// </summary>
    internal bool ShowTrayMenuForDebug()
    {
        if (_trayMenu is null) return false;
        ShowTrayMenu();
        return true;
    }

    /// <summary>
    /// 调试用：把已弹出的托盘菜单渲染成 PNG。菜单现在是 WPF 的可视树（不再是原生窗口），
    /// <c>RenderTargetBitmap</c> 抓得到 —— 与 Avalonia 侧的 <c>shot-tray</c> 行为对齐。
    /// </summary>
    internal string ShotTrayMenuForDebug(string path)
    {
        if (_trayMenu is null) return "ERR no tray menu";
        // tray-right 与 shot-tray 是两条独立消息，中间菜单可能已被别的输入事件关掉；
        // 抓图前先确保它是开着的（Avalonia 侧的 shot-tray 也是自己去找 TrayPopupRoot）。
        if (!_trayMenu.IsOpen) ShowTrayMenu();

        _trayMenu.UpdateLayout();
        var w = (int)Math.Ceiling(_trayMenu.ActualWidth);
        var h = (int)Math.Ceiling(_trayMenu.ActualHeight);
        if (w <= 0 || h <= 0) return $"ERR tray menu has no size ({w}x{h})";

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(_trayMenu);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
        return $"OK {path} w={w} h={h}";
    }

    private void OnOpenPetRequested() => OpenPetWindow();

    /// <summary>
    /// 深浅色切换。<b>这里刻意什么都不做</b> —— 不是没写完，是 Avalonia 那边本来也没有浅色。
    ///
    /// <para>查过：<c>design/mockup.html</c> 里<b>根本没有</b> <c>:root[data-theme="light"]</c>
    /// 这段，设计稿就是纯深色的；<c>Theme/Tokens.axaml</c> 是平铺字典、没有
    /// <c>ThemeDictionaries</c> 的 Light 变体，而 App 里又没挂 FluentTheme。
    /// 所以 Avalonia 侧 <c>ApplyTheme</c> 那句 <c>RequestedThemeVariant = Light</c>
    /// 实际是个 <b>no-op</b>，唯一看得见的副作用是 VM 里
    /// <c>BgArt = PixelArt.CreateBackground(IsDark)</c> 换了背景像素画。</para>
    ///
    /// <para>那份背景图是 VM 属性、走绑定，切主题时自己就刷新了，WPF 侧不需要额外动作。
    /// 真要补浅色调色板，得先在设计稿里定一套 light token，两个平台一起加 ——
    /// 单给 WPF 加会直接破坏两边的像素级一致性。</para>
    /// </summary>
    private void OnThemeChanged(bool dark) =>
        Console.WriteLine($"[Theme] 切到{(dark ? "深色" : "浅色")}：调色板只有深色一套（与 Avalonia 一致），仅背景像素画随 VM 绑定刷新。");

    // ==========================================================
    // 对话框输入
    // ==========================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 换图 / 调完色：像素换了，取景的余量取决于图片原生尺寸，也可能跟着换 ⇒ 重挂位图 + 重算取景。
        if (e.PropertyName == nameof(MainWindowViewModel.BgCustomArt))
        {
            ApplyCustomBackgroundImage();
            ApplyBackgroundFraming();
            return;
        }

        // 换片 / 清空视频：重建画刷并挂到两个元素上。
        if (e.PropertyName == nameof(MainWindowViewModel.VideoBackgroundPath))
        {
            ApplyVideoBackground();
            return;
        }

        // 音量（含"静音 = 音量 0"那一格）：只改播放器音量，不用重新换片。
        // 但它也决定"离开首页要不要继续播"，所以播放门控要跟着重算一遍。
        // VideoBackgroundSound 现在只是音量的别名，VM 会连着发一声 —— 两个名字走同一条分支，
        // 省得"一个更新了另一个没更新"再分叉一次。
        if (e.PropertyName is nameof(MainWindowViewModel.VideoBackgroundVolume)
                         or nameof(MainWindowViewModel.VideoBackgroundSound))
        {
            _videoBackground.Volume = _vm?.VideoBackgroundVolume ?? 0;
            UpdateVideoPlayback();
            return;
        }

        // 首页才有背景，离开首页就把视频暂停。
        if (e.PropertyName == nameof(MainWindowViewModel.IsOnHome))
        {
            UpdateVideoPlayback();
            // 背景可见性刚变，图标色要立刻跟上，不能等下一个 600ms 的采样节拍。
            UpdateWindowButtonTone();
            return;
        }

        // 音量浮层每次**开**都要重新对一次喇叭：它横向位置是算出来的，而"算"这件事不能只挂在
        // 鼠标进入那一下 —— 从别的路径打开（调试动词、以后加快捷键）就会停在右边框上。
        if (e.PropertyName == nameof(MainWindowViewModel.IsVolumePopOpen) && _vm.IsVolumePopOpen)
        {
            RepositionVolumePanel();
            return;
        }

        // 背景调节窗口只认这一个标志（见 SyncBackgroundTuningWindow）。
        if (e.PropertyName == nameof(MainWindowViewModel.IsBgTuningOpen))
        {
            // 开窗顺手刷一次画中画：视频的原生分辨率要等 MediaOpened 才知道，
            // 那一刻没有 PropertyChanged 落到取景上，借"打开"这个时机补一次。
            UpdateBackgroundShot();
            SyncBackgroundTuningWindow();
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.IsDialogOpen) || _vm is null || !_vm.IsDialogOpen) return;

        // 输入框才是这个对话框存在的理由，所以它拿焦点并全选。
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Input,
            new Action(() =>
            {
                if (_vm is null || !_vm.IsDialogOpen || !_vm.DialogHasInput) return;
                DialogInputBox.Focus();
                DialogInputBox.SelectAll();
            }));
    }

    private void OnDialogInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm?.ConfirmDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm?.CancelDialogCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ==========================================================
    // 窗口行为
    // ==========================================================

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // DragMove 会一路阻塞到松开左键；拖动中抛异常会被 WPF 吞掉，所以包一层。
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    // ==========================================================
    // 背景拖动平移（弹层打开时，在那块全窗口背板上）
    // ==========================================================

    private bool _panning;
    private bool _panMoved;
    private Point _panStart;
    private double _panBaseX;
    private double _panBaseY;

    /// <summary>按下不关弹层 —— 关是"抬起且没拖动"的语义（见 <see cref="OnBackgroundPanUp"/>）。</summary>
    private void OnBackgroundPanDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;

        _panning = true;
        _panMoved = false;
        _panStart = e.GetPosition(this);
        _panBaseX = _vm.BgPanX;
        _panBaseY = _vm.BgPanY;

        // 捕获在背板自己身上（不是窗口）：捕获元素才会直接收到 MouseMove/Up，
        // 而这几个处理函数就挂在背板上。
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnBackgroundPanMove(object sender, MouseEventArgs e)
    {
        if (!_panning || _vm is null || e.LeftButton != MouseButtonState.Pressed) return;

        var p = e.GetPosition(this);
        var dx = p.X - _panStart.X;
        var dy = p.Y - _panStart.Y;

        // 3px 死区（和桌宠拖拽同一套阈值）：没超过就还当作"点了一下背景"，不产生位移。
        if (!_panMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
        _panMoved = true;

        var nat = PanContentSize();
        if (nat.Width <= 0 || nat.Height <= 0) return;

        // 只需要 PanStep（1% 平移等于多少屏幕像素），偏移本身用不上，所以 pan 传 0。
        var f = BackgroundFrame.Compute(Shell.ActualWidth, Shell.ActualHeight,
                                        nat.Width, nat.Height, 0, 0, _vm.BgZoom);
        // 余量为 0 的轴不动：cover 之后总有一个轴正好贴边（16:9 的图在 1.64:1 的壳里纵向有余量、
        // 横向没有），硬拖那一轴只会把画面推出可视区。
        if (f.PanStepX > 0) _vm.BgPanX = _panBaseX + dx / f.PanStepX;
        if (f.PanStepY > 0) _vm.BgPanY = _panBaseY + dy / f.PanStepY;
    }

    private void OnBackgroundPanUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;

        _panning = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnBackgroundPanLostCapture(object sender, MouseEventArgs e) => _panning = false;

    /// <summary>滚轮缩放：一格 10%。滑杆搬去调节窗之后，缩放只剩这一条路。</summary>
    private void OnBackgroundWheel(object sender, MouseWheelEventArgs e)
    {
        var vm = _vm;
        if (vm is null || !vm.HasCustomBackground) return;

        // VM 的 setter 自己会夹回 100..300，这里先夹一次是为了滚到顶时不白改一次属性。
        vm.BgZoom = Math.Clamp(vm.BgZoom + (e.Delta > 0 ? 10 : -10),
                               BackgroundFrame.MinZoom, BackgroundFrame.MaxZoom);
        e.Handled = true;
    }

    /// <summary>取景要看的内容尺寸：自选图片用解码后的像素，视频用播放器的原生分辨率。</summary>
    private Size PanContentSize()
    {
        if (_vm?.BgCustomArt is { } art) return new Size(art.Width, art.Height);
        return _videoBackground.NaturalSize;
    }

    // ==========================================================
    // 提示胶囊与画中画的页面内拖动
    // ==========================================================
    // 两个元素默认靠"对齐 + Margin"摆在壳里，一被抓住就换成左上角定位 —— 之后改 Margin
    // 就是纯粹的跟手，不会再被对齐方式拽回去。位置不落盘：每次开机回到默认摆位。

    private FrameworkElement? _floating;
    private Point _floatGrab;

    private void OnHintPillMouseDown(object sender, MouseButtonEventArgs e) =>
        BeginFloatDrag(sender as FrameworkElement, e);

    private void OnBackgroundShotMouseDown(object sender, MouseButtonEventArgs e) =>
        BeginFloatDrag(sender as FrameworkElement, e);

    private void BeginFloatDrag(FrameworkElement? el, MouseButtonEventArgs e)
    {
        if (el is null || Shell is null) return;

        var top = el.TranslatePoint(new Point(0, 0), Shell);
        var at = e.GetPosition(Shell);
        _floatGrab = new Point(at.X - top.X, at.Y - top.Y);   // 抓的是哪儿，别跳

        el.HorizontalAlignment = HorizontalAlignment.Left;
        el.VerticalAlignment = VerticalAlignment.Top;
        el.Margin = new Thickness(top.X, top.Y, 0, 0);

        _floating = el;
        el.CaptureMouse();
        e.Handled = true;
    }

    private void OnFloatDragMove(object sender, MouseEventArgs e)
    {
        if (_floating is null || e.LeftButton != MouseButtonState.Pressed) return;

        var at = e.GetPosition(Shell);
        var w = _floating.ActualWidth;
        var h = _floating.ActualHeight;
        _floating.Margin = new Thickness(
            Math.Clamp(at.X - _floatGrab.X, 8, Math.Max(8, Shell.ActualWidth - w - 8)),
            Math.Clamp(at.Y - _floatGrab.Y, 8, Math.Max(8, Shell.ActualHeight - h - 8)),
            0, 0);
    }

    private void OnFloatDragEnd(object sender, MouseButtonEventArgs e) => EndFloatDrag(sender as FrameworkElement);

    private void OnFloatDragLostCapture(object sender, MouseEventArgs e) => EndFloatDrag(sender as FrameworkElement);

    private void EndFloatDrag(FrameworkElement? el)
    {
        if (_floating is null) return;
        _floating = null;
        el?.ReleaseMouseCapture();
    }

    // ==========================================================
    // 背景调节窗口（独立小窗）
    // ==========================================================

    private Views.BackgroundTuningWindow? _bgTuning;

    /// <summary>壳外那圈投影留白的宽度 —— 摆调节窗时要按可见的壳边对齐，不是按窗口矩形。</summary>
    private const double ShellInset = 60;

    /// <summary>
    /// 让调节窗的可见状态严格跟着 <c>IsBgTuningOpen</c>：只有这一处 Show / Hide。
    /// 调节窗自己关窗口会留下"标志说开着、窗却没了"的假状态，所以它的关闭按钮走
    /// <c>CloseBgTuningCommand</c> 改标志，绕回来由这里收掉。
    /// </summary>
    private void SyncBackgroundTuningWindow()
    {
        var vm = _vm;
        if (vm is null) return;

        if (!vm.IsBgTuningOpen)
        {
            if (_bgTuning is { IsVisible: true }) _bgTuning.Hide();
            return;
        }

        if (_bgTuning is null)
        {
            var win = new Views.BackgroundTuningWindow { Owner = this, DataContext = vm };
            win.PickBackgroundRequested += PickBackground;
            win.PickVideoRequested += PickVideo;
            win.SyncWallpaperEngineRequested += SyncWallpaperEngineFromTuning;
            win.Moved += (x, y) =>
            {
                vm.BgTuningX = x;
                vm.BgTuningY = y;
                vm.BgTuningLeft = x + win.Width / 2 < Left + Width / 2;
            };
            // 用户从任务管理器之类把它关掉时丢掉引用，下次开重新建一个。
            win.Closed += (_, _) => _bgTuning = null;
            _bgTuning = win;
        }

        PlaceBackgroundTuningWindow(_bgTuning);
        _bgTuning.Show();
    }

    /// <summary>把调节窗贴到主窗口旁边：贴哪一侧看那侧的屏幕余量，谁矮的窗口都不能被推出工作区。</summary>
    private void PlaceBackgroundTuningWindow(Window win)
    {
        var area = SystemParameters.WorkArea;
        const double gap = 8;

        if (_vm?.BgTuningX is { } savedX && _vm.BgTuningY is { } savedY)
        {
            // 用户拖过就回老地方，但显示器可能变了 —— 夹回工作区，别让窗口落在不存在的屏幕上。
            win.Left = Math.Clamp(savedX, area.Left, Math.Max(area.Left, area.Right - win.Width));
            win.Top = Math.Clamp(savedY, area.Top, Math.Max(area.Top, area.Bottom - win.Height));
            return;
        }

        win.Top = Math.Clamp(Top + ShellInset, area.Top, Math.Max(area.Top, area.Bottom - win.Height));

        var right = Left + Width - ShellInset + gap;
        if (right + win.Width <= area.Right)
        {
            win.Left = right;
            return;
        }

        // 右侧放不下就翻左边（主窗口被拖到靠右边缘时就是这个情形）。
        win.Left = Math.Max(area.Left, Left + ShellInset - gap - win.Width);
    }

    private void SyncWallpaperEngineFromTuning() => _vm?.SyncWallpaperEngineCommand.Execute(null);

    /// <summary>调试桥用：抓调节窗得拿到它自己的引用（抓主窗口看不见另一扇窗）。没开就是 null。</summary>
    public Window? BackgroundTuningWindowForDebug => _bgTuning is { IsVisible: true } w ? w : null;

    /// <summary>
    /// 主窗口最小化 / 收进托盘时把调节窗一起收掉，回来时再放它出来。
    /// 只在"标志说该开着"时才动手，否则会把一个本来关着的窗给 Show 出来。
    /// </summary>
    private void SyncBackgroundTuningVisibility()
    {
        if (_vm?.IsBgTuningOpen != true || _bgTuning is not { } win) return;

        if (IsVisible && WindowState == WindowState.Normal) win.Show();
        else win.Hide();
    }

    /// <summary>
    /// 主窗口被拖走时带着调节窗一起走 —— 但<b>只限用户没自己摆过</b>的那次自动贴位。
    /// 摆过的话它的位置是用户定的，再跟着跑就是跟丢了。
    /// </summary>
    private void FollowMainWindowIfNotPlaced()
    {
        if (_vm?.BgTuningX is null && _bgTuning is { IsVisible: true } win) PlaceBackgroundTuningWindow(win);
    }

    private void OnPopoverBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseAllPopoversCommand.Execute(null);

    private void OnDialogBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        // 点外面是取消而不是确认，对话框就不可能被误提交。
        _vm?.CancelDialogCommand.Execute(null);

    private void OnInstConfigBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseInstanceConfigCommand.Execute(null);

    // ==========================================================
    // 文件选择
    // ==========================================================

    private void OnPickBackgroundClick(object sender, RoutedEventArgs e) => PickBackground();

    /// <summary>
    /// 选背景图片。设置页那颗按钮和背景调节窗里那颗走的是同一个方法 ——
    /// 文件框的 owner 始终是主窗口（调节窗只是替它转发动作，不该抢这个位置）。
    /// </summary>
    private void PickBackground()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetBackgroundCommand.Execute(dialog.FileName);
    }

    private void OnPickVideoBackgroundClick(object sender, RoutedEventArgs e) => PickVideo();

    /// <summary>选视频背景。同上，两个入口共用。</summary>
    private void PickVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择视频背景",
            Filter = "视频|*.mp4;*.webm;*.avi;*.mkv;*.mov;*.m4v;*.wmv|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetVideoBackgroundCommand.Execute(dialog.FileName);
    }

    /// <summary>
    /// 同步 Wallpaper Engine 当前壁纸。<b>整个过程都在 VM 里</b>（探测、判定类型、兜底预览图），
    /// 视图只负责触发 —— 那边不弹文件选择框，所以没有平台相关的东西要做。
    /// </summary>
    private void OnSyncWallpaperEngineClick(object sender, RoutedEventArgs e) =>
        _vm?.SyncWallpaperEngineCommand.Execute(null);

    private void OnPickJavaClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 javaw.exe",
            Filter = "Java 运行时|javaw.exe;java.exe|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetJavaPathCommand.Execute(dialog.FileName);
    }

    private void OnPickInstJavaClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 javaw.exe",
            Filter = "Java 运行时|javaw.exe;java.exe|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetInstJavaPathCommand.Execute(dialog.FileName);
    }

    // ==========================================================
    // Shift 蹲下
    // ==========================================================

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SkinPreview.Sneaking = true;
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SkinPreview.Sneaking = false;
    }
}
