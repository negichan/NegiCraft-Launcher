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
using System.Windows.Threading;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Theme;
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
    private bool _bgToneDirty = true;
    private bool _bgToneQueued;
    private bool _dragPerfHold;

    /// <summary>图标现在是不是亮底档（深色图标）。</summary>
    private bool _winCtrlLight;

    /// <summary>最近一次采到的平均相对亮度，只在调试桥上用。</summary>
    private double? _lastLum;

    public MainWindow()
    {
        InitializeComponent();
        ApplyPerformanceModeIfNeeded();
        InitializeSettingsViews();
        SetupTrayIcon();
        DataContextChanged += OnDataContextChangedHandler;

        // 图标浮层的宽高由格子自己撑开，没法再写死 HorizontalOffset；回调里拿到的
        // popupSize 就是当前内容的实际尺寸，右沿对齐铅笔按钮、往下让 6。
        IconPickerPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + 6),
                PopupPrimaryAxis.Horizontal),
        };

        // 自选壁纸与视频同构：一个 brush 挂两处元素，取景只改 brush（见 ApplyBackgroundFraming）。
        BgCustomImage.Fill = _customBackground;
        SidebarCustomImage.Fill = _customBackground;

        _videoBackground.Failed += OnVideoBackgroundFailed;
        // 视频的原生尺寸要等 MediaOpened 才可信，那一刻重算一次画中画（之前是 16:9 占位）。
        _videoBackground.Opened += () =>
        {
            UpdateBackgroundShot();
            InvalidateWindowButtonTone();
        };
        IsVisibleChanged += (_, _) => UpdateVideoPlayback();

        // 音量浮层是"喇叭中心 − 浮层宽/2"算出来的，窗口一改尺寸那个中心就变了。
        // 只在鼠标进喇叭时算一次的话，改过窗口宽度后再悬停，箭头会指到别处去。
        SizeChanged += (_, _) => RepositionVolumePanel();

        // 首页 3D 模型的位置存的是比例，所以每次首页有了新尺寸都要重摆一次；
        // 拖拽本身只报增量，怎么换算、存哪儿都由这边定（见 LayoutHomeSkinPreview）。
        PageHome.SizeChanged += (_, _) => LayoutHomeSkinPreview();
        SkinPreview.PositionDragged += OnHomeSkinPreviewDragged;

        // 实例页的列数按页宽算（UniformGrid 只吃列数，不会自己算）。
        PageInstances.SizeChanged += (_, _) => UpdateInstanceColumns();

        // 背景调节窗跟着主窗口的可见性走：主窗口最小化或收进托盘时它不能一个人留在桌面上，
        // 还原时又得自己回来。挂在事件上而不是挂在 OnMinimizeClick / OnCloseClick 里 ——
        // "启动后隐藏启动器"那条路是 VM 直接调 Hide() 的，只盯按钮会漏。
        IsVisibleChanged += (_, _) => SyncBackgroundTuningVisibility();
        StateChanged += (_, _) => SyncBackgroundTuningVisibility();
        LocationChanged += (_, _) => FollowMainWindowIfNotPlaced();

        // 只有视频壁纸才需要这个轮询（静态背景走 InvalidateWindowButtonTone 按需采）。
        // 2s 一次而不是 600ms：单次采样实测 43ms（≈丢 2~3 帧），因为它要让 WPF 把整窗
        // 背景重新栅格化一遍 —— VisualBrush 不会只渲染 Viewbox 那一小块，采样区写多小
        // 都一样。视频壁纸的取色本来就是"看起来差不多就行"，没必要为此每秒抖两次。
        _bgToneTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(2000),
        };
        _bgToneTimer.Tick += (_, _) => UpdateWindowButtonTone();
        IsVisibleChanged += (_, _) => SyncToneSampling();
        StateChanged += (_, _) => SyncToneSampling();
        // 窗口在后台时没人看那几个图标，别让那 43ms 按时打断（视频还照样在放）。
        // 切回来时先标脏再恢复轮询：在后台这段时间背景可能已经变了。
        Deactivated += (_, _) => SyncToneSampling();
        Activated += (_, _) =>
        {
            _bgToneDirty = true;
            SyncToneSampling();
        };

        // 悬停浮层的开合时序不在这里：见 Controls/HoverReveal.cs。

        // 首帧就要定色，不能等第一个 tick —— 否则窗口会先闪一下错色的图标。
        Loaded += (_, _) => SyncToneSampling();

        // 首页小人的落点要在排版彻底落定之后再摆一次：障碍是量出来的，量得太早会把人推歪，
        // 而推开之后没人再把它摆回来（Avalonia 侧实测踩过，见那边 Opened 里的注释）。
        ContentRendered += (_, _) => LayoutHomeSkinPreview();
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

    /// <summary>
    /// 下载列表卡片同理：行的 hover 底色是满幅矩形，不裁圆角就会在卡片四角顶出直角。
    /// 半径和 XAML 里的 <c>CornerRadius="14"</c> 一致。
    /// </summary>
    private void OnDlListCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Border card)
            card.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 14, 14);
    }

    // ==========================================================
    // 下载列表：卡片外面那根 4px 滚动条
    // ==========================================================

    private ScrollViewer? _dlListViewer;
    private bool _dlScrollSyncing;

    /// <summary>
    /// ListBox 自己的滚动条藏在卡片里，稿子要的是卡片外面一根独立的细条。
    /// 这里把内部 ScrollViewer 的四个数抄给外面那根 ScrollBar；条要不要露头由可滚高度决定。
    /// </summary>
    private void OnDlListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is ScrollViewer viewer) _dlListViewer = viewer;
        if (DlListScrollBar is null) return;

        _dlScrollSyncing = true;
        DlListScrollBar.Maximum = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
        DlListScrollBar.ViewportSize = e.ViewportHeight;
        DlListScrollBar.Value = e.VerticalOffset;
        DlListScrollBar.Visibility = e.ExtentHeight > e.ViewportHeight + 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
        _dlScrollSyncing = false;
    }

    private void OnDlListScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dlScrollSyncing) return;
        _dlListViewer?.ScrollToVerticalOffset(e.NewValue);
    }

    // ==========================================================
    // 首页 3D 模型：位置（左键拖）
    // ==========================================================

    /// <summary>没拖过时模型站在设计稿的锚点上 —— 主页的像素回归基准钉在这里，动不得。</summary>
    private const double HomeSkinAnchorLeft = 92;
    private const double HomeSkinAnchorTop = 44;

    /// <summary>
    /// 把"想要的舞台落点"夹成合法落点：身体不出首页，也不压到侧栏、停靠卡与右上角那排上。
    /// 数学在共享层 <c>HomeStageLayout</c> 一份，两端撞同一处会停在同一像素上。
    /// </summary>
    private (double X, double Y) ClampHomeSkinPlace(double wantX, double wantY)
    {
        var pageW = PageHome.ActualWidth;
        var pageH = PageHome.ActualHeight;
        if (pageW <= 0 || pageH <= 0) return (wantX, wantY);   // 还没排版（或不在首页）

        var f = SkinPreview.Footprint;
        return HomeStageLayout.Clamp(
            wantX, wantY, pageW, pageH,
            new PageBox(f.X, f.Y, f.Width, f.Height),
            PageBoxOf(Sidebar), PageBoxOf(DockCard), PageBoxOf(TopRightControls));
    }

    /// <summary>
    /// 元素在首页坐标里的矩形。侧栏、停靠卡和右上角那排都是<b>量出来的</b>，不是写死的数 ——
    /// 它们改边距、改宽度（那排的宽度还随喇叭在不在变），这里的障碍跟着变。
    /// </summary>
    private PageBox PageBoxOf(FrameworkElement element)
    {
        var p = element.TranslatePoint(new Point(0, 0), PageHome);
        return new PageBox(p.X, p.Y, element.RenderSize.Width, element.RenderSize.Height);
    }

    /// <summary>调试桥用：撞墙验收要判"停在障碍外沿对不对"，先得看得见这几块量成了什么。</summary>
    public string HomeSkinKeepOutForDebug =>
        $"sidebar={PageBoxOf(Sidebar)} dock={PageBoxOf(DockCard)} topRight={PageBoxOf(TopRightControls)}";

    /// <summary>按存着（或复位成 null）的比例重摆一次。比例是<b>身体左上角</b>相对首页宽高的比。</summary>
    private void LayoutHomeSkinPreview()
    {
        if (_vm is null || PageHome.ActualWidth <= 0 || PageHome.ActualHeight <= 0) return;

        var f = SkinPreview.Footprint;
        var (x, y) = ClampHomeSkinPlace(
            _vm.HomeSkinModelX is { } xf ? xf * PageHome.ActualWidth - f.X : HomeSkinAnchorLeft,
            _vm.HomeSkinModelY is { } yf ? yf * PageHome.ActualHeight - f.Y : HomeSkinAnchorTop);

        Canvas.SetLeft(SkinPreview, x);
        Canvas.SetTop(SkinPreview, y);
    }

    /// <summary>左键拖来的增量（DIP）：就地挪，再把新位置换成比例写回 VM —— 落盘归 VM，视图不知道设置文件。</summary>
    private void OnHomeSkinPreviewDragged(double dx, double dy)
    {
        var pageW = PageHome.ActualWidth;
        var pageH = PageHome.ActualHeight;
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

    // ==========================================================
    // 实例页：列数、⋯ 菜单
    // ==========================================================

    /// <summary>卡片的最小宽度（稿子里的 <c>minmax(268px,1fr)</c>）。放不下就少一列，不硬缩。</summary>
    private const double InstanceCardMinWidth = 268;
    private const double InstanceGutter = 12;

    /// <summary>
    /// 按页宽算列数交给 UniformGrid：卡片既要等宽（自适应拉伸）又要等高
    /// （底行那句「最近游玩」是贴底摆的），WrapPanel 两样都给不了。
    /// </summary>
    private void UpdateInstanceColumns()
    {
        var w = PageInstances.ActualWidth;
        if (_vm is null || w <= 0) return;   // 还没排版（或不在实例页），等下一次尺寸变化

        _vm.InstanceColumns = Math.Max(1, (int)((w + InstanceGutter) / (InstanceCardMinWidth + InstanceGutter)));
    }

    /// <summary>
    /// 「⋯」只是把这一行自己那条右键菜单就地开起来 —— 菜单只写一份，右键和按钮走同一个。
    /// 往上找带 ContextMenu 的那个 Button（卡片本体 / 列表行 / 目录行都是它），逻辑树在这一段是通的。
    /// </summary>
    private void OnRowMoreClick(object sender, RoutedEventArgs e)
    {
        for (FrameworkElement? node = sender as FrameworkElement; node is not null; node = node.Parent as FrameworkElement)
        {
            if (node is not Button { ContextMenu: { } } card) continue;

            card.ContextMenu.PlacementTarget = card;
            card.ContextMenu.IsOpen = true;
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// 列表行双击 = 直接启动（老玩家的肌肉记忆）。第一击已经触发过单击选中，
    /// 这里只补第二击；启动守卫（运行中禁用）在 VM 的命令里统一拦。
    /// </summary>
    private void OnInstanceRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstanceModel instance } && _vm is { } vm)
        {
            vm.LaunchInstanceCommand.Execute(instance);
            e.Handled = true;
        }
    }

    // ==========================================================
    // 账户信息卡（悬停侧栏底部头像）
    // ==========================================================

    // 和音量浮层同一套开合时序，也一并搬到 Controls/HoverReveal.cs 了。
    // 犹豫期 130ms / 收合轮询 140ms 这两个数字现在写在 XAML 的附加属性上。

    /// <summary>
    /// 播放器该不该在跑。
    ///
    /// <para>背景画面现在每一页都在，"在不在首页"就不再是暂停的理由了；只剩一条：
    /// 窗口收进托盘时暂停 —— 那时候没人看也没人听。</para>
    /// </summary>
    private void UpdateVideoPlayback()
    {
        _videoBackground.SetActive(IsVisible);
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
            _vm.ExitApplicationRequested -= ExitApplication;
            _vm.RestartApplicationRequested -= RestartApplication;
            _vm.OpenPetRequested -= OnOpenPetRequested;
            _vm.RecallPetRequested -= ClosePetWindow;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ThemeChanged -= OnThemeChanged;
            _vm.MotionPreferenceChanged -= ApplyMotionPreference;
            _vm.BackgroundFramingChanged -= ApplyBackgroundFraming;
            _vm.RequestAddModFile -= OnRequestAddModFile;
            _vm.RequestAddResourcePackFile -= OnRequestAddResourcePackFile;
            _vm.RequestExportInstance -= OnRequestExportInstance;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.HideWindowRequested += Hide;
            _vm.ShowWindowRequested += Restore;
            _vm.ExitApplicationRequested += ExitApplication;
            _vm.RestartApplicationRequested += RestartApplication;
            _vm.OpenPetRequested += OnOpenPetRequested;
            _vm.RecallPetRequested += ClosePetWindow;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.ThemeChanged += OnThemeChanged;
            _vm.MotionPreferenceChanged += ApplyMotionPreference;

            // 取景只改变换、不碰像素，所以单独一条事件：拖动平移时一次调色都不该发生。
            _vm.BackgroundFramingChanged += ApplyBackgroundFraming;
            _vm.RequestAddModFile += OnRequestAddModFile;
            _vm.RequestAddResourcePackFile += OnRequestAddResourcePackFile;
            _vm.RequestExportInstance += OnRequestExportInstance;

            // 动画档位要在第一帧之前就定下来：DataContext 换完才定的话，
            // 首页那一下入场动画会按旧的（可能是"关"）跑。
            ApplyMotionPreference(_vm.MotionPreference);

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
    /// 设置页那一档动画（跟随系统 / 总是开 / 总是关）落到动画引擎上。
    ///
    /// <para>判据在共享层（<c>MotionPreferenceExtensions.Override</c>），
    /// 这边只负责把结果写进 <c>MotionEngine.Override</c>。
    /// Theme 工程刻意不引用 ViewModels（桌宠也要引它），所以判据不放在那边。</para>
    /// </summary>
    private void ApplyMotionPreference(MotionPreference preference) =>
        MotionEngine.Override = preference.Override();

    /// <summary>
    /// 设置页里那块 <c>Debug</c> 分区的挂载点（导航项 + 卡片）。
    /// <para><b>刻意做成 partial void</b>：实现在 <c>MainWindow.Debug.cs</c>，整个文件包在
    /// <c>#if DEBUG</c> 里。Release 构建下那个文件编译为空，编译器会把这条声明和所有调用
    /// 一并消掉 —— 于是"Debug 分区不参与发版编译"这件事由编译器保证，而不是靠运行期隐藏。</para>
    /// <para>XAML 没有 <c>#if</c>，所以这块 UI 只能代码建（见 <c>MainWindow.Debug.cs</c>）。</para>
    /// </summary>
    partial void OnDebugSectionAttach(MainWindowViewModel? vm);

    // ==========================================================
    // 对话框输入
    // ==========================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentSettingsTab))
        {
            PageSettingsScroll.ScrollToTop();
        }

        if (e.PropertyName is nameof(MainWindowViewModel.BgArt)
                         or nameof(MainWindowViewModel.BgBrightness)
                         or nameof(MainWindowViewModel.BgBlur)
                         or nameof(MainWindowViewModel.IsCustomBackground)
                         or nameof(MainWindowViewModel.IsVideoBackground))
        {
            InvalidateWindowButtonTone();
            SyncToneSampling();
        }
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
        // VideoBackgroundSound 现在只是音量的别名，VM 会连着发一声 —— 两个名字走同一条分支，
        // 省得"一个更新了另一个没更新"再分叉一次。
        if (e.PropertyName is nameof(MainWindowViewModel.VideoBackgroundVolume)
                         or nameof(MainWindowViewModel.VideoBackgroundSound))
        {
            _videoBackground.Volume = _vm?.VideoBackgroundVolume ?? 0;
            return;
        }

        // 音量浮层每次**开**都要重新对一次喇叭：它横向位置是算出来的，而"算"这件事不能只挂在
        // 鼠标进入那一下 —— 从别的路径打开（调试动词、以后加快捷键）就会停在右边框上。
        if (e.PropertyName == nameof(MainWindowViewModel.IsVolumePopOpen) && _vm?.IsVolumePopOpen == true)
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

    private void OnPopoverBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseAllPopoversCommand.Execute(null);

    private void OnDialogBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        // 点外面是取消而不是确认，对话框就不可能被误提交。
        _vm?.CancelDialogCommand.Execute(null);

    private void OnInstallBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseInstallSheetCommand.Execute(null);

    private void OnInstallIconBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseInstallIconPickerCommand.Execute(null);

    private void OnInstConfigBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseInstanceConfigCommand.Execute(null);

    /// <summary>点询问窗外面 = 取消：关闭这种事不该有一个"点空白也能触发"的出口。</summary>
    private void OnClosePromptBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CancelCloseCommand.Execute(null);

    // ==========================================================
    // Shift 蹲下
    // ==========================================================

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SkinPreview.Sneaking = true;

        // 关闭询问窗是拦在"退出"前面的一道，Esc 关它等于按取消 —— 不能让 Esc 顺带把窗口也关了。
        if (e.Key == Key.Escape && _vm is { IsClosePromptOpen: true })
        {
            _vm.CancelCloseCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Esc 的第二档：先收浮层（下拉 / 图标选择 / 运行时选择），都收干净了在管理页里才是「返回」。
        // 对话框开着时不动 —— 它有自己的 Esc（取消），这里抢了反而把确认流程打断。
        if (e.Key == Key.Escape && _vm is { IsDialogOpen: false } vm)
        {
            if (vm.IsInstallIconPickerOpen)
            {
                vm.IsInstallIconPickerOpen = false;
                e.Handled = true;
            }
            else if (vm.IsInstallSheetOpen)
            {
                vm.CloseInstallSheetCommand.Execute(null);
                e.Handled = true;
            }
            else if (vm.IsIconPickerOpen || vm.IsInstJavaChooserOpen)
            {
                vm.IsIconPickerOpen = false;
                vm.IsInstJavaChooserOpen = false;
                e.Handled = true;
            }
            else if (vm.HasOpenPopover)
            {
                vm.CloseAllPopoversCommand.Execute(null);
                e.Handled = true;
            }
            else if (vm.CurrentPage == "instance_manage")
            {
                vm.BackToInstancesCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SkinPreview.Sneaking = false;
    }
}
