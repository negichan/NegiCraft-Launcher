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
using Avalonia.LogicalTree;
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

        // 实例页的列数按页宽算：UniformGrid 只会把宽度平摊给它拿到的列数，
        // "几列才不挤"只有视图量得出来（与 WPF 侧的 UpdateInstanceColumns 同一套）。
        PageInstances.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateInstanceColumns();
        };

        // 拖文件进窗口 = 导入整合包。Avalonia 的这三个是 routed event（WPF 那边是窗口的普通事件），
        // 而事件是从命中的那颗控件往上冒的 —— 挂在窗口上才能不管落在哪一层都收到（AllowDrop 在 XAML 里）。
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnWindowDragLeave);
        AddHandler(DragDrop.DropEvent, OnWindowDrop, handledEventsToo: true);

        // 页面入场过渡：给每个挂着 PageAnim 的面板接上。
        // 样式里那条"只淡入"的动画已经退役（见 MainStyles.axaml 的说明）——
        // 方向要按"上一页 → 下一页"判，样式读不到上一页是谁。
        foreach (var page in this.GetLogicalDescendants().OfType<Panel>()
                     .Where(p => p.Classes.Contains("PageAnim")))
        {
            PageTransition.Attach(page);
        }

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
    /// 把"想要的舞台落点"夹成合法落点：身体不出首页，也不压到侧栏、停靠卡与右上角那排上。
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
            PageBoxOf(Sidebar), PageBoxOf(DockCard), PageBoxOf(TopRightControls));
    }

    /// <summary>
    /// 元素在首页坐标里的矩形。侧栏、停靠卡和右上角那排都是<b>量出来的</b>，不是写死的数 ——
    /// 它们改边距、改宽度，这里的障碍跟着变。
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
        $"sidebar={PageBoxOf(Sidebar)} dock={PageBoxOf(DockCard)} topRight={PageBoxOf(TopRightControls)} " +
        $"page=({PageHome.Bounds.X:0},{PageHome.Bounds.Y:0},{PageHome.Bounds.Width:0}x{PageHome.Bounds.Height:0})";

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


    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.HideWindowRequested -= Hide;
            _vm.ShowWindowRequested -= Restore;
            _vm.ExitApplicationRequested -= ExitApplication;
            _vm.OpenPetRequested -= OnOpenPetRequested;
            _vm.RecallPetRequested -= ClosePetWindow;
            _vm.RequestAddModFile -= OnRequestAddModFile;
            _vm.RequestAddResourcePackFile -= OnRequestAddResourcePackFile;
            _vm.RequestExportInstance -= OnRequestExportInstance;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.BackgroundFramingChanged -= ApplyBackgroundFraming;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.HideWindowRequested += Hide;
            _vm.ShowWindowRequested += Restore;
            _vm.ExitApplicationRequested += ExitApplication;
            _vm.OpenPetRequested += OnOpenPetRequested;
            _vm.RecallPetRequested += ClosePetWindow;
            _vm.RequestAddModFile += OnRequestAddModFile;
            _vm.RequestAddResourcePackFile += OnRequestAddResourcePackFile;
            _vm.RequestExportInstance += OnRequestExportInstance;
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

    // ==========================================================
    // 实例页：列数与「⋯」—— 与 App/MainWindow.xaml.cs 逐段对照
    // ==========================================================

    private const double InstanceCardMinWidth = 268;
    private const double InstanceGutter = 12;

    /// <summary>
    /// 按页宽算列数交给 UniformGrid：卡片既要等宽（自适应拉伸）又要等高
    /// （底行那句「最近游玩」是贴底摆的），WrapPanel 两样都给不了。
    /// </summary>
    private void UpdateInstanceColumns()
    {
        var w = PageInstances.Bounds.Width;
        if (_vm is null || w <= 0) return;   // 还没排版（或不在实例页），等下一次尺寸变化

        _vm.InstanceColumns = Math.Max(1, (int)((w + InstanceGutter) / (InstanceCardMinWidth + InstanceGutter)));
    }

    /// <summary>
    /// 「⋯」只是把这一行自己那条右键菜单就地开起来 —— 菜单只写一份，右键和按钮走同一个。
    /// 往上找带 ContextMenu 的那个 Button（卡片本体 / 列表行 / 目录行都是它）：
    /// Avalonia 的可视树在这一段是通的。
    /// </summary>
    private void OnRowMoreClick(object? sender, RoutedEventArgs e)
    {
        for (Visual? node = sender as Visual; node is not null; node = node.GetVisualParent())
        {
            if (node is not Button { ContextMenu: { } } card) continue;

            card.ContextMenu.Open(card);
            e.Handled = true;
            return;
        }
    }

    /// <summary>
    /// 列表行双击 = 直接启动（老玩家的肌肉记忆）。第一击已经触发过单击选中，
    /// 这里只补第二击；启动守卫（运行中禁用）在 VM 的命令里统一拦。
    /// </summary>
    private void OnInstanceRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is StyledElement { DataContext: InstanceModel instance } && _vm is { } vm)
        {
            vm.LaunchInstanceCommand.Execute(instance);
            e.Handled = true;
        }
    }

    private void OnInstConfigBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseInstanceConfigCommand.Execute(null);
    }

    /// <summary>点询问窗外面 = 取消：关闭这种事不该有一个"点空白也能触发"的出口。</summary>
    private void OnClosePromptBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CancelCloseCommand.Execute(null);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            SkinPreview.Sneaking = true;
        }

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
            if (vm.IsIconPickerOpen || vm.IsInstJavaChooserOpen)
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

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            SkinPreview.Sneaking = false;
        }
    }
}