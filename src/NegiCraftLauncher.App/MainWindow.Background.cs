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
using System.Windows.Threading;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;
using Forms = System.Windows.Forms;

namespace NegiCraftLauncher.App;

/// <summary>
/// 主窗口的背景线：视频背景、照片背景、取景（平移 + 缩放）、以及那个独立的背景调节小窗。
///
/// <para>这几段共用同一批状态（<c>_videoBackground</c> / <c>_shotBrush</c> / <c>_coverageBoxes</c> /
/// <c>_panning</c> / <c>_bgTuning</c>），跨文件反而更难看懂 —— 所以按「一条线」而不是按
/// 「一个对象」来切。从 <c>MainWindow.xaml.cs</c> 原样搬来。</para>
/// </summary>
public partial class MainWindow
{
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
        InvalidateWindowButtonTone();
        SyncToneSampling();
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
        InvalidateWindowButtonTone();
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

    /// <summary>
    /// 同步 Wallpaper Engine 当前壁纸。<b>整个过程都在 VM 里</b>（探测、判定类型、兜底预览图），
    /// 视图只负责触发 —— 那边不弹文件选择框，所以没有平台相关的东西要做。
    /// </summary>
    /// <remarks>internal：设置里那颗按钮在样式字典的模板里，和 <see cref="BackgroundTuningWindow"/>
    /// 的转发共用这一个入口。</remarks>
    internal void SyncWallpaperEngineFromTuning() => _vm?.SyncWallpaperEngineCommand.Execute(null);

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
    private bool _followQueued;

    private void FollowMainWindowIfNotPlaced()
    {
        if (_vm?.BgTuningX is not null || _bgTuning is not { IsVisible: true }) return;

        // 拖动中 LocationChanged 每帧都来，而摆位只关心最终值 ⇒ 合到本轮排版之后摆一次：
        // 一帧最多一次，落下来的是最后那个位置。
        if (_followQueued) return;
        _followQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _followQueued = false;
            if (_vm?.BgTuningX is null && _bgTuning is { IsVisible: true } win) PlaceBackgroundTuningWindow(win);
        }, DispatcherPriority.Loaded);
    }

}
