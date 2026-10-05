using System.ComponentModel;
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

        _videoBackground.Failed += OnVideoBackgroundFailed;
        IsVisibleChanged += (_, _) => UpdateVideoPlayback();

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
    /// <para>半径用 <c>22.5</c> 而非 <c>22</c>：WPF 的 <c>BorderBrush</c> 描边画在
    /// <c>CornerRadius</c> 的外侧（描边宽 1 ⇒ 半径 21.5~22.5），clip=22 会把外侧
    /// 那半像素裁掉，描边看起来细一半。22.5 让描边完整、圆角仍贴齐。</para>
    /// </summary>
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Shell.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 22.5, 22.5);
    }

    /// <summary>
    /// 侧栏磨砂背板同理：<c>ClipToBounds=True</c> 只裁矩形，圆角得自己给一条裁剪几何，
    /// 否则被 <c>BlurEffect</c> 放大的背景图会从四个圆角外漏出来（看着就是矩形 + 角上一圈糊）。
    /// 半径要和 XAML 里的 <c>CornerRadius="18"</c> 保持一致。
    /// </summary>
    private void OnSidebarBackdropSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SidebarBackdrop.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 18, 18);
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
        // 先定静音与音量再换片：Load 内部会在 Open 之后断言一次，顺序反了会白做一次。
        _videoBackground.Sound = _vm?.VideoBackgroundSound ?? false;
        _videoBackground.Volume = _vm?.VideoBackgroundVolume ?? 100;
        _videoBackground.Load(_vm?.VideoBackgroundPath);

        var brush = _videoBackground.HasVideo ? _videoBackground.Brush : null;
        BgVideo.Fill = brush;
        SidebarVideo.Fill = brush;

        UpdateVideoPlayback();
    }

    /// <summary>调试桥用：视频播放器此刻真实的音频状态。</summary>
    public string VideoBackgroundDebug => _videoBackground.DebugState;

    /// <summary>调试桥用：右上角喇叭按钮与音量浮层此刻的可见性 / 悬停状态 / 横向对齐。</summary>
    public string SpeakerDebug
    {
        get
        {
            var w = VolumePanel.ActualWidth;
            // 面板中心直接按布局算（HorizontalAlignment=Right + Margin.Right），
            // 不走 TransformToVisual —— Pop 动画期间 RenderTransform 会把结果带偏。
            var panelCx = ActualWidth - VolumePanel.Margin.Right - w / 2;
            var btnCx = VideoSoundButton.ActualWidth > 0
                ? VideoSoundButton.TransformToVisual(this)
                    .Transform(new Point(VideoSoundButton.ActualWidth / 2, 0)).X
                : double.NaN;

            return $"visible={VideoSoundButton.IsVisible} pop={_vm?.IsVolumePopOpen} " +
                   $"btnHover={VideoSoundButton.IsMouseOver} panelHover={VolumePanel.IsMouseOver} " +
                   $"panel={w:0.#}x{VolumePanel.ActualHeight:0.#} " +
                   $"cx={panelCx:0.#}/{btnCx:0.#} slider={VideoVolumeSlider.Value:0.#}";
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

    /// <summary>右上角喇叭：点一下切静音。</summary>
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
    /// <para>另外还要一段宽限：<c>Pop</c> 的开合动画要 370ms，飞行期间它会把浮层的
    /// <c>IsHitTestVisible</c> 压成 false —— 这段时间 <c>IsMouseOver</c> 恒为 false，
    /// 不宽限的话浮层刚出来就被看门狗收掉了（见 <see cref="VolumeOpenGraceMs"/>）。</para>
    /// </summary>
    private readonly DispatcherTimer _volumeHoverTimer;

    private const int VolumeHoverPollMs = 180;
    private const int VolumeOpenGraceMs = 520;

    private DateTime _volumeOpenedAt;

    private void OnVideoSoundMouseEnter(object sender, MouseEventArgs e) => ShowVolumePanel();

    private void OnVolumePanelMouseEnter(object sender, MouseEventArgs e) => ShowVolumePanel();

    private void ShowVolumePanel()
    {
        if (_vm is null) return;
        if (!_vm.IsVolumePopOpen) _volumeOpenedAt = DateTime.UtcNow;
        RepositionVolumePanel();
        _vm.IsVolumePopOpen = true;
        _volumeHoverTimer.Start();
    }

    /// <summary>
    /// 把浮层横向摆到"喇叭正下方"。
    ///
    /// <para><b>为什么不在 XAML 里写死右边距</b>：浮层靠 <c>HorizontalAlignment="Right"</c> +
    /// 右边距定位，而浮层宽度是内容决定的（轨道宽度、百分比文字的宽度都会影响）。
    /// 写死一个数字，改了里面任何一处尺寸箭头就偏了。所以每次开之前按
    /// "喇叭中心 − 浮层宽/2" 现算。</para>
    /// </summary>
    private void RepositionVolumePanel()
    {
        // 浮层在可视树里（只是 Opacity=0），正常早就量过了；保险起见补一次布局。
        if (VolumePanel.ActualWidth < 1) UpdateLayout();

        var w = VolumePanel.ActualWidth;
        if (w < 1) return;

        var center = VideoSoundButton.TransformToVisual(this)
            .Transform(new Point(VideoSoundButton.ActualWidth / 2, 0)).X;

        VolumePanel.Margin = new Thickness(0, VolumePanel.Margin.Top,
            Math.Max(0, ActualWidth - center - w / 2), 0);
    }

    private void CheckVolumeHover()
    {
        if (_vm is null || !_vm.IsVolumePopOpen)
        {
            _volumeHoverTimer.Stop();
            return;
        }

        // 开合动画还没落定（这期间浮层不可命中），先别判。
        if ((DateTime.UtcNow - _volumeOpenedAt).TotalMilliseconds < VolumeOpenGraceMs) return;

        if (VideoSoundButton.IsMouseOver || VolumePanel.IsMouseOver) return;

        _volumeHoverTimer.Stop();
        _vm.IsVolumePopOpen = false;
    }

    /// <summary>
    /// 背景只在首页可见，所以离开首页或窗口收进托盘时就暂停 ——
    /// 否则切到设置页还在后台解码 4K，白烧 CPU。
    /// </summary>
    private void UpdateVideoPlayback() =>
        _videoBackground.SetActive(IsVisible && (_vm?.IsOnHome ?? false));

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

            // 设置里存着视频壁纸的话，DataContext 一到就该把它挂上。
            ApplyVideoBackground();
        }
    }

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
        // 换片 / 清空视频：重建画刷并挂到两个元素上。
        if (e.PropertyName == nameof(MainWindowViewModel.VideoBackgroundPath))
        {
            ApplyVideoBackground();
            return;
        }

        // 声音开关：只改播放器的静音状态，不用重新换片。
        if (e.PropertyName == nameof(MainWindowViewModel.VideoBackgroundSound))
        {
            _videoBackground.Sound = _vm?.VideoBackgroundSound ?? false;
            return;
        }

        // 音量条：同理，只改播放器音量。
        if (e.PropertyName == nameof(MainWindowViewModel.VideoBackgroundVolume))
        {
            _videoBackground.Volume = _vm?.VideoBackgroundVolume ?? 100;
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

    private void OnPopoverBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseAllPopoversCommand.Execute(null);

    private void OnBgPopBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseBgPopCommand.Execute(null);

    private void OnDialogBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        // 点外面是取消而不是确认，对话框就不可能被误提交。
        _vm?.CancelDialogCommand.Execute(null);

    private void OnInstConfigBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseInstanceConfigCommand.Execute(null);

    // ==========================================================
    // 文件选择
    // ==========================================================

    private void OnPickBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetBackgroundCommand.Execute(dialog.FileName);
    }

    private void OnPickVideoBackgroundClick(object sender, RoutedEventArgs e)
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
