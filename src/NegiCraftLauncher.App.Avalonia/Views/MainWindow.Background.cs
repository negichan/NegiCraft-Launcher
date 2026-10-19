using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NegiCraftLauncher.App.Avalonia.Converters;
using NegiCraftLauncher.ViewModels;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Pet.Avalonia;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.App.Avalonia.Views;

/// <summary>
/// 背景线（与 WPF 侧 <c>App/MainWindow.Background.cs</c> 一一对应）：照片背景的取景
/// （平移 + 缩放）、画中画截图、以及那个独立的背景调节小窗。
///
/// <para>按「一条线」而不是「一个对象」来切：这些方法共用 <c>_panning</c> / <c>_bgTuning</c> /
/// <c>_shotBrush</c>，拆散反而难跟。从 <c>MainWindow.axaml.cs</c> 原样搬来。</para>
/// </summary>
public partial class MainWindow
{
    // ==========================================================
    // 背景取景（平移 + 缩放）—— 与 App/MainWindow.xaml.cs 逐段对照
    // ==========================================================

    /// <summary>
    /// 自选壁纸的画刷。几何全在<b>绝对</b>的 SourceRect / DestinationRect 上，主层与侧栏共用
    /// 这一个实例 ⇒ 改一处两处同步，对齐由构造保证（两个元素的本地矩形都等于壳矩形）。
    /// </summary>
    private readonly ImageBrush _customBackground = new()
    {
        Stretch = Stretch.Fill,
        TileMode = TileMode.None,
    };

    /// <summary>复用视图那个转换器：像素格式（Bgra8888 预乘）的约定只有一处实现。</summary>
    private static readonly PixelBufferToBitmapConverter ToBitmap = new();

    private void ApplyBackgroundFraming()
    {
        var vm = _vm;
        if (vm?.BgCustomArt is not { } art) return;

        var shellW = Shell.Bounds.Width;
        var shellH = Shell.Bounds.Height;
        if (shellW <= 0 || shellH <= 0) return;   // 还没排版，等 Shell 的 Bounds 变化再来

        var box = BackgroundFrame.ComputeVideoViewbox(
            shellW, shellH, art.Width, art.Height, vm.BgPanX, vm.BgPanY, vm.BgZoom);

        _customBackground.DestinationRect = new RelativeRect(0, 0, shellW, shellH, RelativeUnit.Absolute);
        _customBackground.SourceRect = new RelativeRect(box.X, box.Y, box.Width, box.Height, RelativeUnit.Absolute);

        UpdateBackgroundShot(shellW, shellH, art, vm);
    }

    /// <summary>画中画那格的位图刷：整张图铺满，不带取景 —— 取景是白框负责表达的那部分。</summary>
    private readonly ImageBrush _shotBrush = new() { Stretch = Stretch.Fill };

    /// <summary>
    /// 右上角那格画中画：整张图 contain 进来，白框 = 窗口真正看得见的那一块。
    /// 与 WPF 侧同一个 <see cref="BackgroundFrame.ComputeCoverage"/>、同一组入参 ⇒ 两端画的是同一件事。
    /// 位图直接复用主层那张（零额外解码 / 零额外转换）。
    /// </summary>
    private void UpdateBackgroundShot(double shellW, double shellH, PixelBuffer art, MainWindowViewModel vm)
    {
        var b = BackgroundFrame.ComputeCoverage(
            BgShot.Width, BgShot.Height, shellW, shellH,
            art.Width, art.Height, vm.BgPanX, vm.BgPanY, vm.BgZoom);

        PlaceInShot(BgShotImage, b.ImageX, b.ImageY, b.ImageW, b.ImageH);
        PlaceInShot(BgShotView, b.ViewX, b.ViewY, b.ViewW, b.ViewH);
        _shotBrush.Source = _customBackground.Source;
        BgShotImage.Fill = _shotBrush;
        _coverageBoxes = b;
    }

    /// <summary>调试桥用：chip 最后一次画出来的几何（与 WPF 侧同名读数，两端对着看）。</summary>
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

    private static void PlaceInShot(global::Avalonia.Controls.Shapes.Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = w;
        r.Height = h;
    }

    private void ApplyCustomBackgroundImage()
    {
        _customBackground.Source = _vm?.BgCustomArt is { } art
            ? (IImageBrushSource?)ToBitmap.Convert(art, typeof(IImageBrushSource), null, CultureInfo.CurrentCulture)
            : null;
        ApplyBackgroundFraming();
    }

    // ---- 拖动平移（弹层打开时，在那块全窗口背板上）----

    private bool _panning;
    private bool _panMoved;
    private Point _panStart;
    private double _panBaseX;
    private double _panBaseY;

    private void OnBackgroundPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null) return;

        var point = e.GetCurrentPoint(sender as Visual);
        if (!point.Properties.IsLeftButtonPressed) return;

        _panning = true;
        _panMoved = false;
        _panStart = point.Position;
        _panBaseX = _vm.BgPanX;
        _panBaseY = _vm.BgPanY;

        // 捕获在背板自己身上：捕获元素才会直接收到后续 PointerMoved/Released。
        e.Pointer.Capture(sender as InputElement);
        e.Handled = true;
    }

    private void OnBackgroundPanMoved(object? sender, PointerEventArgs e)
    {
        if (!_panning || _vm is null) return;

        var p = e.GetCurrentPoint(sender as Visual).Position;
        var dx = p.X - _panStart.X;
        var dy = p.Y - _panStart.Y;

        // 3px 死区：没超过就还当作"点了一下背景"。
        if (!_panMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
        _panMoved = true;

        if (_vm.BgCustomArt is not { } art) return;   // Avalonia 侧没有视频面，只有图片能拖

        // 只需要 PanStep（1% 平移等于多少屏幕像素），偏移本身用不上 ⇒ pan 传 0。
        var f = BackgroundFrame.Compute(Shell.Bounds.Width, Shell.Bounds.Height,
                                        art.Width, art.Height, 0, 0, _vm.BgZoom);
        // 余量为 0 的轴不动：cover 之后总有一个轴正好贴边，硬拖只会把画面推出可视区。
        if (f.PanStepX > 0) _vm.BgPanX = _panBaseX + dx / f.PanStepX;
        if (f.PanStepY > 0) _vm.BgPanY = _panBaseY + dy / f.PanStepY;
    }

    private void OnBackgroundPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_panning) return;

        _panning = false;
        e.Pointer.Capture(null);
    }

    private void OnBackgroundPanCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _panning = false;

    /// <summary>滚轮缩放：一格 10%。滑杆搬去调节窗之后，缩放只剩这一条路。</summary>
    private void OnBackgroundWheel(object? sender, PointerWheelEventArgs e)
    {
        var vm = _vm;
        if (vm is null || !vm.HasCustomBackground) return;

        var step = Math.Sign(e.Delta.Y) * 10;
        if (step == 0) return;
        vm.BgZoom = Math.Clamp(vm.BgZoom + step, BackgroundFrame.MinZoom, BackgroundFrame.MaxZoom);
        e.Handled = true;
    }

    // ==========================================================
    // 背景调节窗口（独立小窗）—— 与 WPF 侧逐段对照
    // ==========================================================

    private BackgroundTuningWindow? _bgTuning;

    /// <summary>壳外那圈投影留白的宽度 —— 摆调节窗要按可见的壳边对齐，不是按窗口矩形。</summary>
    private const double ShellInset = 60;

    /// <summary>
    /// 让调节窗的可见状态严格跟着 <c>IsBgTuningOpen</c>：只有这一处 Show / Hide。
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
            // Owner 在 Avalonia 里是受保护成员，不能像 WPF 那样从别处赋值 —— Show(owner) 就是它的写法。
            var win = new BackgroundTuningWindow { DataContext = vm };
            win.PickBackgroundRequested += () => _ = PickBackgroundAsync();
            win.SyncWallpaperEngineRequested += () => vm.SyncWallpaperEngineCommand.Execute(null);
            win.Moved += p =>
            {
                // 存的是 DIP 屏幕坐标（与 WPF 侧同一口径）：Avalonia 的 Position 是物理像素。
                var s = Scale;
                vm.BgTuningX = p.X / s;
                vm.BgTuningY = p.Y / s;
                vm.BgTuningLeft = p.X + win.Width * s / 2 < Position.X + Bounds.Width * s / 2;
            };
            win.Closed += (_, _) => _bgTuning = null;
            _bgTuning = win;
        }

        PlaceBackgroundTuningWindow(_bgTuning);
        _bgTuning.Show(this);
    }

    /// <summary>当前缩放（DIP → 物理像素）。拿不到屏幕信息时按 1 算，宁可摆位略偏也不崩。</summary>
    private double Scale => Screens.ScreenFromWindow(this)?.Scaling ?? 1.0;

    /// <summary>把调节窗贴到主窗口旁边：贴哪一侧看那侧的屏幕余量，且不能被推出工作区。</summary>
    private void PlaceBackgroundTuningWindow(BackgroundTuningWindow win)
    {
        var s = Scale;
        // 拿不到屏幕信息（无头探针、异常时机）时退回"主窗口所在的那块虚拟桌面"，
        // 至少窗口不会跑到屏幕外面去。
        var area = Screens.ScreenFromWindow(this)?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
        var gap = (int)Math.Round(8 * s);
        var winW = (int)Math.Round(win.Width * s);
        var winH = (int)Math.Round((win.Bounds.Height > 0 ? win.Bounds.Height : 612) * s);

        if (_vm?.BgTuningX is { } savedX && _vm.BgTuningY is { } savedY)
        {
            // 用户拖过就回老地方，但显示器可能变了 —— 夹回工作区，别落在不存在的屏幕上。
            PlaceAt(win, new PixelPoint(
                (int)Math.Clamp(savedX * s, area.X, Math.Max(area.X, area.Right - winW)),
                (int)Math.Clamp(savedY * s, area.Y, Math.Max(area.Y, area.Bottom - winH))));
            return;
        }

        var shellTop = Position.Y + (int)Math.Round(ShellInset * s);
        var shellRight = Position.X + (int)Math.Round((Bounds.Width - ShellInset) * s);
        var shellLeft = Position.X + (int)Math.Round(ShellInset * s);
        var x = shellRight + gap;
        if (x + winW > area.Right) x = Math.Max(area.X, shellLeft - gap - winW);

        PlaceAt(win, new PixelPoint(x, Math.Clamp(shellTop, area.Y, Math.Max(area.Y, area.Bottom - winH))));
    }

    /// <summary>
    /// 摆位，并且<b>不把这次移动算成"用户拖过"</b>。
    ///
    /// <para>Avalonia 的 <c>PositionChanged</c> 是在属性更新之后才发的，不是赋值那一行内 ——
    /// 所以 <c>Placing</c> 标志不能在下一行就清掉，否则自动摆位会顺着 <c>Moved</c> 存进设置，
    /// 窗口从此不再跟主窗口走（实测：一次开机就把 <c>backgroundTuningX/Y</c> 写死了）。</para>
    /// </summary>
    private void PlaceAt(BackgroundTuningWindow win, PixelPoint p)
    {
        win.Placing = true;
        win.Position = p;
        Dispatcher.UIThread.Post(() => win.Placing = false, DispatcherPriority.Loaded);
    }

    /// <summary>主窗口最小化 / 收进托盘时把调节窗一起收掉，回来时再放它出来。</summary>
    private void SyncBackgroundTuningVisibility()
    {
        if (_vm?.IsBgTuningOpen != true || _bgTuning is not { } win) return;

        if (IsVisible && WindowState == WindowState.Normal) win.Show();
        else win.Hide();
    }

    /// <summary>主窗口被拖走时带着调节窗一起走 —— 但只限用户没自己摆过的那次自动贴位。</summary>
    private void FollowMainWindowIfNotPlaced()
    {
        if (_vm?.BgTuningX is null && _bgTuning is { IsVisible: true } win) PlaceBackgroundTuningWindow(win);
    }

    /// <summary>调试桥用：抓调节窗得拿到它自己的引用。没开就是 null。</summary>
    public Window? BackgroundTuningWindowForDebug => _bgTuning is { IsVisible: true } w ? w : null;

    public PetWindow? PetWindowInstance => _petWindow;

}
