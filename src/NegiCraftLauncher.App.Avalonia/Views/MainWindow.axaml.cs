using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NegiCraftLauncher.App.Avalonia.Converters;
using NegiCraftLauncher.ViewModels;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Pet.Avalonia;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.App.Avalonia.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private PetWindow? _petWindow;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _petTrayMenuItem;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
        SetupTrayIcon();

        // 自选壁纸与 WPF 侧同构：一个 brush 挂两处元素，取景只改 brush。
        BgCustomImage.Fill = _customBackground;
        SidebarCustomImage.Fill = _customBackground;
        BgShotImage.Fill = _shotBrush;
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) ApplyBackgroundFraming();
        };

        // 背景调节窗跟着主窗口的可见性走（与 WPF 侧同一套理由：只盯按钮会漏掉 VM 直接 Hide 那条路）。
        // Avalonia 的 IsVisibleChanged 是虚方法不是事件，所以走 AvaloniaObject.PropertyChanged。
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty) SyncBackgroundTuningVisibility();
        };
        PositionChanged += (_, _) => FollowMainWindowIfNotPlaced();

        // 首页 3D 模型的位置存的是比例，所以首页每有一个新尺寸都要重摆一次；拖拽本身只报增量。
        PageHome.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) LayoutHomeSkinPreview();
        };
        SkinPreview.PositionDragged += OnHomeSkinPreviewDragged;

        // 头几趟布局里 TranslatePoint 还给不出稳定的页内坐标（首页那圈 -100/-22 的负边距还没进到
        // 变换链），障碍会量歪 —— 而一旦把锚点当成"压到侧栏"推开，之后就再没人把它摆回来。
        // 排版落定之后再摆一次，落点以这一趟为准。
        Opened += (_, _) => Dispatcher.UIThread.Post(LayoutHomeSkinPreview, DispatcherPriority.Loaded);
    }

    // ==========================================================
    // 首页 3D 模型：位置（左键拖）—— 与 App/MainWindow.xaml.cs 逐段对照
    // ==========================================================

    /// <summary>没拖过时模型站在设计稿的锚点上 —— 主页的像素回归基准钉在这里，动不得。</summary>
    private const double HomeSkinAnchorLeft = 92;
    private const double HomeSkinAnchorTop = 44;

    /// <summary>
    /// 把"想要的舞台落点"夹成合法落点：身体不出首页，也不压到侧栏与停靠卡上。
    /// 数学在共享层 <c>HomeStageLayout</c> 一份，两端撞同一处会停在同一像素上。
    /// </summary>
    private (double X, double Y) ClampHomeSkinPlace(double wantX, double wantY)
    {
        var pageW = PageHome.Bounds.Width;
        var pageH = PageHome.Bounds.Height;
        if (pageW <= 0 || pageH <= 0) return (wantX, wantY);   // 还没排版（或在别的页）

        var f = SkinPreview.Footprint;
        return HomeStageLayout.Clamp(
            wantX, wantY, pageW, pageH,
            new PageBox(f.X, f.Y, f.Width, f.Height),
            PageBoxOf(Sidebar), PageBoxOf(DockCard));
    }

    /// <summary>
    /// 元素在首页坐标里的矩形。侧栏和停靠卡是<b>量出来的</b>，不是写死的数 ——
    /// 它们改边距、改宽度（停靠卡的高度还跟着"启动中"那张卡变），这里的障碍跟着变。
    /// </summary>
    private PageBox PageBoxOf(Visual element)
    {
        var p = element.TranslatePoint(new Point(0, 0), PageHome) ?? default;
        return new PageBox(p.X, p.Y, element.Bounds.Width, element.Bounds.Height);
    }

    /// <summary>按存着（或复位成 null）的比例重摆一次。比例是<b>身体左上角</b>相对首页宽高的比。</summary>
    private void LayoutHomeSkinPreview()
    {
        var pageW = PageHome.Bounds.Width;
        var pageH = PageHome.Bounds.Height;
        if (_vm is null || pageW <= 0 || pageH <= 0) return;   // 还没排版（或在别的页），等下一次 Bounds 变化

        var f = SkinPreview.Footprint;
        var (x, y) = ClampHomeSkinPlace(
            _vm.HomeSkinModelX is { } xf ? xf * pageW - f.X : HomeSkinAnchorLeft,
            _vm.HomeSkinModelY is { } yf ? yf * pageH - f.Y : HomeSkinAnchorTop);

        Canvas.SetLeft(SkinPreview, x);
        Canvas.SetTop(SkinPreview, y);
    }

    /// <summary>左键拖来的增量（DIP）：就地挪，再把新位置换成比例写回 VM —— 落盘归 VM，视图不知道设置文件。</summary>
    private void OnHomeSkinPreviewDragged(double dx, double dy)
    {
        var pageW = PageHome.Bounds.Width;
        var pageH = PageHome.Bounds.Height;
        if (_vm is null || pageW <= 0 || pageH <= 0) return;

        var (x, y) = ClampHomeSkinPlace(Canvas.GetLeft(SkinPreview) + dx, Canvas.GetTop(SkinPreview) + dy);
        Canvas.SetLeft(SkinPreview, x);
        Canvas.SetTop(SkinPreview, y);

        // 存身体的比例：窗口再怎么缩放都站在同一处，而且恒在 0..1 之内 ——
        // 存舞台左上角的话，贴到左边缘会是个负数，读回来夹一次就和屏幕上不一致了。
        var f = SkinPreview.Footprint;
        _vm.HomeSkinModelX = (x + f.X) / pageW;
        _vm.HomeSkinModelY = (y + f.Y) / pageH;
    }

    /// <summary>调试桥入口：喂一对 DIP 增量，走的正是左键拖动那条路（见 <see cref="OnHomeSkinPreviewDragged"/>）。</summary>
    public void DragHomeSkinPreviewForDebug(double dx, double dy) => OnHomeSkinPreviewDragged(dx, dy);

    /// <summary>调试桥用：撞墙验收要判"停在障碍外沿对不对"，先得看得见这两块量成了什么。</summary>
    public string HomeSkinKeepOutForDebug =>
        $"sidebar={PageBoxOf(Sidebar)} dock={PageBoxOf(DockCard)} " +
        $"page=({PageHome.Bounds.X:0},{PageHome.Bounds.Y:0},{PageHome.Bounds.Width:0}x{PageHome.Bounds.Height:0})";

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
    // 提示胶囊与画中画的页面内拖动（与 WPF 侧同一套做法）
    // ==========================================================

    private Control? _floating;
    private Point _floatGrab;

    private void OnHintPillPressed(object? sender, PointerPressedEventArgs e) => BeginFloatDrag(sender as Control, e);

    private void OnBackgroundShotPressed(object? sender, PointerPressedEventArgs e) => BeginFloatDrag(sender as Control, e);

    private void BeginFloatDrag(Control? el, PointerPressedEventArgs e)
    {
        if (el is null || !e.GetCurrentPoint(el).Properties.IsLeftButtonPressed) return;

        var at = e.GetCurrentPoint(Shell).Position;
        var top = el.TranslatePoint(new Point(0, 0), Shell) ?? new Point(at.X, at.Y);
        _floatGrab = new Point(at.X - top.X, at.Y - top.Y);   // 抓的是哪儿，别跳

        // 类里有个同名属性 HorizontalAlignment / VerticalAlignment，得用全名才够到枚举类型。
        el.HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Left;
        el.VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Top;
        el.Margin = new Thickness(top.X, top.Y, 0, 0);

        _floating = el;
        e.Pointer.Capture(el);
        e.Handled = true;
    }

    private void OnFloatDragMove(object? sender, PointerEventArgs e)
    {
        if (_floating is null) return;

        var at = e.GetCurrentPoint(Shell).Position;
        var w = _floating.Bounds.Width;
        var h = _floating.Bounds.Height;
        _floating.Margin = new Thickness(
            Math.Clamp(at.X - _floatGrab.X, 8, Math.Max(8, Shell.Bounds.Width - w - 8)),
            Math.Clamp(at.Y - _floatGrab.Y, 8, Math.Max(8, Shell.Bounds.Height - h - 8)),
            0, 0);
    }

    private void OnFloatDragEnd(object? sender, PointerReleasedEventArgs e)
    {
        if (_floating is null) return;
        _floating = null;
        e.Pointer.Capture(null);
    }

    private void OnFloatDragCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _floating = null;

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

    private void SetupTrayIcon()
    {
        try
        {
            var menu = new NativeMenu();

            var openLauncherItem = new NativeMenuItem { Header = "打开启动器" };
            openLauncherItem.Click += (_, _) => Restore();
            openLauncherItem.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(Restore);
            menu.Items.Add(openLauncherItem);

            _petTrayMenuItem = new NativeMenuItem { Header = "桌面宠物" };
            _petTrayMenuItem.Click += (_, _) => TogglePetWindow();
            _petTrayMenuItem.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(TogglePetWindow);
            menu.Items.Add(_petTrayMenuItem);

            menu.Items.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem { Header = "退出启动器" };
            exitItem.Click += (_, _) => ExitApplication();
            exitItem.Command = new CommunityToolkit.Mvvm.Input.RelayCommand(ExitApplication);
            menu.Items.Add(exitItem);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NegiCraftLauncher.Avalonia/Assets/app.ico"))),
                ToolTipText = "NegiCraft Launcher",
                IsVisible = true,
                Menu = menu
            };
            _trayIcon.Clicked += (_, _) => Restore();

            TrayIcon.SetIcons(Application.Current!, new TrayIcons { _trayIcon });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TrayIcon] Init error: {ex.Message}");
        }
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        _petWindow?.Close();
        _petWindow = null;
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.HideWindowRequested -= Hide;
            _vm.ShowWindowRequested -= Restore;
            _vm.OpenPetRequested -= OnOpenPetRequested;
            _vm.RecallPetRequested -= ClosePetWindow;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
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

            // 取景只改变换、不碰像素，所以单独一条事件：拖动平移时一次调色都不该发生。
            _vm.BackgroundFramingChanged += ApplyBackgroundFraming;
            ApplyCustomBackgroundImage();
            LayoutHomeSkinPreview();
        }

        OnDebugSectionAttach(_vm);
    }

    /// <summary>
    /// Mount point for the <c>Debug</c> section of the settings page (nav entry + card).
    /// <para><b>Deliberately a <c>partial void</c></b>: the implementation lives in
    /// <c>Views/MainWindow.Debug.cs</c>, which is wrapped entirely in <c>#if DEBUG</c>. In a
    /// Release build that file compiles to nothing, so the compiler erases this declaration and
    /// every call to it — "the Debug section is not compiled into a release" is guaranteed by the
    /// compiler rather than by hiding things at runtime.</para>
    /// <para>XAML has no <c>#if</c>, so this UI can only be built in code
    /// (see <c>MainWindow.Debug.cs</c>).</para>
    /// </summary>
    partial void OnDebugSectionAttach(MainWindowViewModel? vm);

    public void TogglePetWindow()
    {
        if (_petWindow != null && _petWindow.IsVisible)
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
        if (_petWindow != null && _petWindow.IsVisible)
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
            _petTrayMenuItem.Header = (_petWindow != null && _petWindow.IsVisible) ? "收起桌宠" : "桌面宠物";
        }
    }

    private void OnOpenPetRequested() => OpenPetWindow();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 换图 / 调完色：重挂位图 + 重算取景（余量取决于图片原生尺寸，换图就变）。
        if (e.PropertyName == nameof(MainWindowViewModel.BgCustomArt))
        {
            ApplyCustomBackgroundImage();
            return;
        }

        // 背景调节窗口只认这一个标志（见 SyncBackgroundTuningWindow）。
        if (e.PropertyName == nameof(MainWindowViewModel.IsBgTuningOpen))
        {
            SyncBackgroundTuningWindow();
            return;
        }

        // 位置比例变了（拖完一格 / 按「复位位置」/ 关掉开关顺带清掉），或开关本身变了：重摆一次。
        // 开关必须跟着走 —— 清掉的是"存着的比例"，Canvas 上还留着上一次拖出来的像素，
        // 不重摆就会看见重新打开的小人站在老地方。
        if (e.PropertyName is nameof(MainWindowViewModel.HomeSkinModelX)
                         or nameof(MainWindowViewModel.HomeSkinModelY)
                         or nameof(MainWindowViewModel.ShowHomeSkinModel))
        {
            LayoutHomeSkinPreview();
            return;
        }

        if (e.PropertyName != nameof(MainWindowViewModel.IsDialogOpen) || !_vm!.IsDialogOpen) return;
        // The prompt is the whole point of the dialog, so it takes focus and starts selected.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_vm.IsDialogOpen || !_vm.DialogHasInput) return;
            DialogInputBox.Focus();
            DialogInputBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnDialogInputKeyDown(object? sender, KeyEventArgs e)
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

    private void Restore()
    {
        IsVisible = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnDragAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnPopoverBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseAllPopoversCommand.Execute(null);
    }

    private async void OnPickBackgroundClick(object? sender, RoutedEventArgs e) => await PickBackgroundAsync();

    /// <summary>
    /// 选背景图片。设置页那颗按钮和背景调节窗里那颗走的是同一个方法 ——
    /// 文件框的 owner 始终是主窗口（调节窗只是替它转发动作，不该抢这个位置）。
    /// </summary>
    private async Task PickBackgroundAsync()
    {
        if (_vm is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择背景图片",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("图片")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });

        if (files.Count > 0)
        {
            _vm.SetBackgroundCommand.Execute(files[0].Path.LocalPath);
        }
    }

    private async void OnPickJavaClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 javaw.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java 运行时") { Patterns = new[] { "javaw.exe", "java.exe" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0)
        {
            _vm.SetJavaPathCommand.Execute(files[0].Path.LocalPath);
        }
    }

    private async void OnPickInstJavaClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 javaw.exe",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Java 运行时") { Patterns = new[] { "javaw.exe", "java.exe" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0)
        {
            _vm.SetInstJavaPathCommand.Execute(files[0].Path.LocalPath);
        }
    }

    private void OnDialogBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking outside cancels rather than confirms, so a dialog can never be committed by accident.
        _vm?.CancelDialogCommand.Execute(null);
    }

    private void OnInstConfigBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseInstanceConfigCommand.Execute(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            SkinPreview.Sneaking = true;
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            SkinPreview.Sneaking = false;
        }
    }
}