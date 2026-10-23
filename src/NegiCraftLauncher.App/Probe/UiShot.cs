using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.Theme;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Probe;

/// <summary>
/// <c>--ui</c>：把真正的主窗口摆在屏幕外跑起来，逐页出图，用来和 Avalonia 版的同页截图对照。
///
/// <para>为什么不开着窗口让人看：验证不该抢用户的焦点（可能正全屏游戏）。窗口
/// <c>Left/Top = -32000</c> + <c>ShowActivated=false</c>，全程不进前台。</para>
///
/// <para>出图走 <see cref="RenderTargetBitmap"/> 直接渲染窗口内容根（见 <c>UseDirectRender</c>），
/// 与 Avalonia 版 <c>DebugBridge</c> 的 <c>shot</c> 同一条路，出图可以直接逐像素对照。</para>
///
/// <para><b>画布尺寸与窗口解耦</b>：屏幕比窗口窄时窗口管理器会把窗口夹小，
/// 但截图统一强制到固定的画布（见 <see cref="ForceCanvas"/> 与 <c>CanvasWidth</c>），
/// 所以像素对照有稳定基准。</para>
///
/// <para><b>不止看截图</b>：还会把 WPF 的绑定错误（<c>PresentationTraceSources.DataBindingSource</c>）
/// 收集进报告，并逐个报出四个页面容器的真实 <c>Visibility</c>。
/// 绑定转换失败时 WPF 不报错、悄悄退回属性默认值 —— 四个页面会一起显示出来，
/// 光看截图容易以为是布局问题。</para>
/// </summary>
internal static class UiShot
{
    private static readonly string ReportPath = Path.Combine(Path.GetTempPath(), "ncl-wpf-ui.txt");

    private static readonly string[] Pages = ["home", "instances", "download", "settings"];

    /// <summary>
    /// 像素对照用的固定画布尺寸，与两端窗口的 Width/Height 一致 —— <b>含壳外那圈投影留白</b>。
    ///
    /// <para>⚠️ 这个数不是壳的 1180x720：窗口内容根是"壳 + 四周 60 留白"，画布按壳的尺寸去
    /// <c>Measure/Arrange</c> 会把留白挤掉、壳缩成 1060x600，于是和 Avalonia 那份 1300x840 的
    /// 基准整体错位（实测整页 13% 差异，看着像回归，其实是探针自己缩了）。
    /// 改窗口留白宽度时这里要跟着改（两端 <c>MainWindow.xaml</c> 的 Width/Height 是唯一真相）。</para>
    /// </summary>
    private const int CanvasWidth = 1300;

    private const int CanvasHeight = 840;

    private static readonly string[] SettingsTabs =
    [
        "游戏", "Java", "下载", "外观", "桌宠", "关于",
#if DEBUG
        // 设置页那块 Debug 分区只在 Debug 构建里存在（见 MainWindow.Debug.cs），探针也跟着多抓一张。
        "Debug",
#endif
    ];

    private static readonly string[] PageContainerNames = ["PageHome", "PageInstances", "PageDownload", "PageSettings"];

    private static readonly BindingErrorCollector Errors = new();

    /// <summary>
    /// 截图走哪条路。<b>默认直接渲染</b>（<c>RenderTargetBitmap.Render(content)</c>）。
    ///
    /// <para>曾经担心"<c>Render(子元素)</c> 会带上父级偏移、小图空白"，所以默认走
    /// <c>VisualBrush</c> + <c>Stretch=None</c> 归零重画。实测下来对<b>窗口内容根</b>
    /// 而言直接渲染的偏移就是 0，出图与 VisualBrush 版逐像素一致（1180x720 全图仅 79 个像素差 &gt;8，
    /// 且都在圆角边缘的抗锯齿上）。</para>
    ///
    /// <para>结论：直接渲染更可信 —— 它是 WPF 真正的合成结果，不过一遍 <c>VisualBrush</c>，
    /// 也就能排除"截图管线自己造出来的伪影"。要和 Avalonia 的 <c>shot</c>
    /// （那边也是直接 <c>RenderTargetBitmap.Render(Window.Content)</c>）做像素对照，就必须用它。
    /// 传 <c>--visualbrush</c> 可切回旧路径。</para>
    /// </summary>
    private static bool UseDirectRender = true;

    public static void Run(string[] args)
    {
        UseDirectRender = Array.IndexOf(args, "--visualbrush") < 0;

        // 绑定错误默认只写到调试输出，重定向到这里才能进报告。
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(Errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        var log = new StringBuilder();
        log.AppendLine("NegiCraft Launcher — WPF 主窗口逐页截图");
        log.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"渲染路径: {(UseDirectRender ? "直接渲染 RenderTargetBitmap.Render(content)" : "VisualBrush 归零重画")}");
        log.AppendLine($"对照基准: Avalonia 版 design/_smoke.ps1 出的 %TEMP%\\ncl-smoke-<page>.png（{CanvasWidth}x{CanvasHeight}，同为直接渲染）");
        log.AppendLine("像素对照: python design/_diff.py <wpf.png> <smoke.png> --shift 6");
        log.AppendLine("（design/ 下的脚本是本地私有工具，不随源码分发 —— 见 design/README.md）");

        MainWindowViewModel vm;
        try
        {
            // 解码器要注入：不注入的话自选壁纸不会出现在探针图里（VM 拿不到像素）。
            vm = new MainWindowViewModel(new Services.WpfBitmapDecoder());
        }
        catch (Exception ex)
        {
            log.AppendLine($"VM 构造失败: {ex}");
            File.WriteAllText(ReportPath, log.ToString());
            Application.Current.Shutdown();
            return;
        }

        var window = new MainWindow
        {
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
            ShowActivated = false,
        };

        window.Show();
        log.AppendLine($"窗口: {window.Width}x{window.Height}  位置 {window.Left},{window.Top}");
        // 尺寸会随 DPI / 屏幕而变，而像素对照要求窗口尺寸稳定 —— 把环境记下来，
        // 对照不通过时先看这里是不是跟前一次不一样。
        var dpi = VisualTreeHelper.GetDpi(window);
        log.AppendLine(
            $"环境: DPI {dpi.DpiScaleX:F4}x{dpi.DpiScaleY:F4} " +
            $"主屏 {SystemParameters.PrimaryScreenWidth:F0}x{SystemParameters.PrimaryScreenHeight:F0} " +
            $"虚拟屏 {SystemParameters.VirtualScreenWidth:F0}x{SystemParameters.VirtualScreenHeight:F0} " +
            $"工作区 {SystemParameters.WorkArea.Width:F0}x{SystemParameters.WorkArea.Height:F0}");

        // 让消息循环先跑起来，等 VM 初始化（皮肤、版本列表都是异步的）。
        _ = RunSequenceAsync(window, vm, log);
    }

    private static async Task RunSequenceAsync(Window window, MainWindowViewModel vm, StringBuilder log)
    {
        await CaptureSequenceAsync(window, vm, log);
        File.WriteAllText(ReportPath, log.ToString());
        Application.Current.Shutdown();
    }

    private static async Task CaptureSequenceAsync(Window window, MainWindowViewModel vm, StringBuilder log)
    {
        await Task.Delay(3000);

        // 窗口尺寸被 WM 夹住时（屏幕比窗口窄）不要紧 —— 截图走 ForceCanvas，
        // 把内容根强行排到画布尺寸，和窗口大小解耦。
        window.Width = CanvasWidth;
        window.Height = CanvasHeight;
        await Task.Delay(200);
        log.AppendLine($"窗口实际 {window.ActualWidth:F0}x{window.ActualHeight:F0}（截图统一强制到 {CanvasWidth}x{CanvasHeight}）");

        foreach (var page in Pages)
        {
            vm.GoCommand.Execute(page);
            await Task.Delay(450);
            await Capture(window, $"ncl-wpf-{page}.png", log);
            AuditPages(window, page, log);
        }

        // 实例图标的预设浮层。⚠️ 它是 Popup（独立 HWND），**不在窗口内容根的渲染树里** ——
        // 截窗口拍不到它，只能单独渲染它的 Child，再拿坐标核对它有没有挂对位置。
        if (vm.VisibleInstances.FirstOrDefault() is { } probeInstance)
        {
            vm.OpenInstanceConfigCommand.Execute(probeInstance);
            await Task.Delay(400);

            // ⚠️ Popup 是独立 HWND，WPF 不允许它停在屏幕外；而探针平时把窗口摆在一万米之外，
            // 那样量出来的「位置差」全是被拉回工作区的距离，量不出对齐关系。所以这一段临时把
            // 窗口挪进屏幕，量完立刻挪回去（窗口 ShowActivated=false，不会抢焦点）。
            var restoreLeft = window.Left;
            var restoreTop = window.Top;
            window.Left = 120;
            window.Top = 120;
            await Task.Delay(250);

            vm.OpenIconPickerCommand.Execute(null);
            await Task.Delay(400);

            // 出场动画必须由 MotionEngine 决定（WPF 的 PopupAnimation 绕开调度，已经删掉了）。
            // 本机系统「显示动画」是关着的，所以两档各强制过一遍：该动的时候开屏还在 0.94，
            // 「总是关」的时候直接满尺寸 —— 只截静态图看不出这件事，只能读属性。
            if (window.FindName("IconPickerPopup") is Popup { Child: FrameworkElement animationTarget } popupForMotion)
            {
                MotionEngine.Override = true;
                vm.IsIconPickerOpen = false;
                await Task.Delay(120);
                vm.OpenIconPickerCommand.Execute(null);
                var starting = animationTarget.RenderTransform is ScaleTransform { ScaleX: < 0.99 };
                log.AppendLine($"  {(starting ? "OK" : "!!")} icon popup 动画: 开屏瞬间 transform=" +
                               $"{animationTarget.RenderTransform?.GetType().Name ?? "null"}、透明度 {animationTarget.Opacity:0.00}");

                await Task.Delay(450);
                var settled = animationTarget.RenderTransform is null && animationTarget.Opacity > 0.99;
                log.AppendLine($"  {(settled ? "OK" : "!!")} icon popup 动画: 落地后 transform=" +
                               $"{animationTarget.RenderTransform?.GetType().Name ?? "null"}、透明度 {animationTarget.Opacity:0.00}");

                MotionEngine.Override = false;
                vm.IsIconPickerOpen = false;
                await Task.Delay(120);
                vm.OpenIconPickerCommand.Execute(null);
                var instant = animationTarget.RenderTransform is null && animationTarget.Opacity > 0.99;
                log.AppendLine($"  {(instant ? "OK" : "!!")} icon popup 动画: 「总是关」时直接满尺寸");
                MotionEngine.Override = null;
                await Task.Delay(150);
            }

            // 混排：前 3 项是加载器标志（loader: 前缀），后面全是方块 —— 顺序错了这里的断言就挂。
            var presets = vm.IconPresets.ToList();
            var logos = presets.Take(3).ToList();
            var blocks = presets.Skip(3).ToList();
            var logosOk = logos.Count == 3 && logos.All(p => p.Key.StartsWith("loader:", StringComparison.Ordinal));
            var blocksOk = blocks.Count > 0 && blocks.All(p => !p.Key.StartsWith("loader:", StringComparison.Ordinal));
            log.AppendLine($"  {(logosOk && blocksOk ? "OK" : "!!")} icon presets: 共 {presets.Count} 项" +
                           $"（标志 {logos.Count} + 方块 {blocks.Count}，标志在前）");

            if (window.FindName("IconPickerPopup") is Popup { Child: FrameworkElement popupChild } popup)
            {
                log.AppendLine($"  {(popup.IsOpen ? "OK" : "!!")} icon popup: IsOpen={popup.IsOpen}，内容 {popupChild.ActualWidth:F0}x{popupChild.ActualHeight:F0}");

                // 位置：右边缘对齐铅笔按钮（横向负偏移就是为这个），纵向落在按钮下面。
                if (window.FindName("IconPickerAnchor") is FrameworkElement anchor)
                {
                    var anchorRight = anchor.PointToScreen(new Point(anchor.ActualWidth, 0)).X;
                    var anchorBottom = anchor.PointToScreen(new Point(0, anchor.ActualHeight)).Y;
                    var popupLeft = popupChild.PointToScreen(new Point(0, 0));
                    var popupRight = popupChild.PointToScreen(new Point(popupChild.ActualWidth, 0)).X;
                    var aligned = Math.Abs(popupRight - anchorRight) <= 3;
                    var below = popupLeft.Y >= anchorBottom - 1;
                    log.AppendLine($"  {(aligned && below ? "OK" : "!!")} icon popup 位置: 右沿差 {popupRight - anchorRight:F1}px、" +
                                   $"上沿-按钮下沿 {popupLeft.Y - anchorBottom:F1}px（屏幕像素）");
                }

                await CaptureVisual(popupChild, "ncl-wpf-inst-icon-popup.png", log);
            }
            else
            {
                log.AppendLine("  !! icon popup: 找不到 IconPickerPopup 或它的内容");
            }

            // 这张图现在的意义是反的：浮层不该再占卡片的高度，下面这些行应该紧贴图标行。
            await Capture(window, "ncl-wpf-inst-icon-picker.png", log);
            vm.IsIconPickerOpen = false;
            window.Left = restoreLeft;
            window.Top = restoreTop;
            vm.GoCommand.Execute("instances");
            await Task.Delay(250);
        }

        // 首页右上角有一块不明亮斑，查一下那底下到底是什么元素。
        vm.GoCommand.Execute("home");
        await Task.Delay(400);
        HitTestAt(window, 1046, 65, log);
        FindInRect(window, new Rect(1000, 40, 80, 70), log);

        await AuditSettingsPage((MainWindow)window, vm, log);
        vm.GoCommand.Execute("settings");
        foreach (var tab in SettingsTabs)
        {
            vm.SelectSettingsTabCommand.Execute(tab);
            await Task.Delay(320);
            await Capture(window, $"ncl-wpf-settings-{tab}.png", log);
            await Capture(window, $"ncl-wpf-settings-{tab}-minimum.png", log, 1080, 740);
            var scroll = (System.Windows.Controls.ScrollViewer)window.FindName("PageSettingsScroll");
            log.AppendLine($"  {(scroll.ExtentWidth <= scroll.ViewportWidth + 1 && scroll.VerticalOffset < 1 ? "OK" : "!!")} settings={tab}: no horizontal overflow, scroll resets");
        }
        vm.SelectSettingsTabCommand.Execute("Java");
        vm.IsJavaChooserOpen = true;
        await Capture(window, "ncl-wpf-settings-Java-expanded-minimum.png", log, 1080, 740);
        vm.IsJavaChooserOpen = false;

        // 弹层
        vm.GoCommand.Execute("home");
        await Task.Delay(400);
        vm.ToggleAccPopCommand.Execute(null);
        await Task.Delay(420);
        await Capture(window, "ncl-wpf-pop-acc.png", log);

        vm.ToggleAccPopCommand.Execute(null);
        vm.ToggleInstPopCommand.Execute(null);
        await Task.Delay(420);
        await Capture(window, "ncl-wpf-pop-inst.png", log);

        vm.ToggleInstPopCommand.Execute(null);
        vm.GoCommand.Execute("download");
        await Task.Delay(400);
        vm.ToggleDlPopCommand.Execute(null);
        await Task.Delay(420);
        await Capture(window, "ncl-wpf-pop-dl.png", log);
        vm.ToggleDlPopCommand.Execute(null);

        // 设置里的「下载」子页（下载中心那张列表）
        vm.GoCommand.Execute("settings");
        vm.SelectSettingsTabCommand.Execute("下载");
        await Task.Delay(400);
        await Capture(window, "ncl-wpf-settings-downloads.png", log);

        AuditNamedElements(window, log);
        ReportErrors(log);

    }

    /// <summary>四个页面容器同一时刻只该有一个是 Visible。</summary>
    private static void AuditPages(Window window, string currentPage, StringBuilder log)
    {
        var visible = new List<string>();
        foreach (var name in PageContainerNames)
        {
            if (window.FindName(name) is not FrameworkElement element) continue;
            if (element.IsVisible) visible.Add(name);
        }

        var flag = visible.Count == 1 ? "OK" : "!!";
        log.AppendLine($"  {flag} page={currentPage}  可见容器: {string.Join(", ", visible)}");
    }

    private static async Task AuditSettingsPage(MainWindow window, MainWindowViewModel vm, StringBuilder log)
    {
        void Check(string label, bool valid) => log.AppendLine($"  {(valid ? "OK" : "!!")} settings page: {label}");

        vm.GoCommand.Execute("download");
        vm.GoCommand.Execute("settings");
        await Task.Delay(300);
        Check("sidebar navigation opens the regular settings page",
            vm.CurrentPage == "settings" && ((FrameworkElement)window.FindName("PageSettings")).IsVisible);
        Check("no settings dialog is left in the tree",
            window.FindName("SettingsOverlay") is null && window.FindName("SettingsDialog") is null);

        vm.SelectSettingsTabCommand.Execute("Java");
        await Task.Delay(120);
        Check("selecting a tab keeps it selected",
            vm.CurrentSettingsTab == "Java");

        AuditPageDirection(vm, log);

        var memory = FindMemorySlider((DependencyObject)window.FindName("PageSettingsCardsHost"));
        var originalMemory = vm.MaxMemoryGb;
        try
        {
            memory.SetCurrentValue(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                originalMemory == 32 ? 31.0 : originalMemory + 1.0);
            await Task.Delay(40);
            Check("editing the memory slider updates the view model",
                vm.MaxMemoryGb == (int)memory.Value);
        }
        finally
        {
            vm.MaxMemoryGb = originalMemory;
        }

        // 设置页是整页模式：换页之后设置页该收起来，而不是留在底下。
        vm.GoCommand.Execute("instances");
        await Task.Delay(350);
        Check("page navigation leaves the settings page",
            vm.CurrentPage == "instances" && !((FrameworkElement)window.FindName("PageSettings")).IsVisible);

        var legacy = new MainWindowViewModel(new Services.WpfBitmapDecoder());
        legacy.GoCommand.Execute("settings");
        Check("settings navigation works from a fresh view model", legacy.CurrentPage == "settings");

        static System.Windows.Controls.Slider FindMemorySlider(DependencyObject root)
        {
            return Find(root) ?? throw new InvalidOperationException("Memory slider not found in settings view.");

            static System.Windows.Controls.Slider? Find(DependencyObject element)
            {
                if (element is System.Windows.Controls.Slider slider &&
                    System.Windows.Data.BindingOperations.GetBinding(slider, System.Windows.Controls.Primitives.RangeBase.ValueProperty)?.Path.Path == "MaxMemoryGb")
                    return slider;
                foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
                    if (Find(child) is { } found) return found;
                return null;
            }
        }
    }

    /// <summary>
    /// 页面切换方向的判据。侧栏从上到下是 首页 / 实例 / 下载，设置在底部，
    /// 实例配置页紧跟实例页（它是点进去的下一层）。
    /// </summary>
    private static void AuditPageDirection(MainWindowViewModel vm, StringBuilder log)
    {
        void Check(string label, bool valid) => log.AppendLine($"  {(valid ? "OK" : "!!")} page direction: {label}");

        // 用一份干净的 VM，别继承探针跑到这儿时的当前页。
        var probe = new MainWindowViewModel(new Services.WpfBitmapDecoder());

        probe.GoCommand.Execute("home");
        probe.GoCommand.Execute("instances");
        Check("going down the sidebar slides in from above",
            probe.LastPageDirection == PageDirection.FromAbove);

        probe.GoCommand.Execute("download");
        Check("down again stays from above",
            probe.LastPageDirection == PageDirection.FromAbove);

        probe.GoCommand.Execute("instances");
        Check("going back up slides in from below",
            probe.LastPageDirection == PageDirection.FromBelow);

        probe.GoCommand.Execute("settings");
        Check("settings sits at the bottom of the sidebar",
            probe.LastPageDirection == PageDirection.FromAbove);

        probe.GoCommand.Execute("home");
        Check("the home page only fades in, whichever way it is entered from",
            probe.LastPageDirection == PageDirection.None);
    }

    /// <summary>把所有带 x:Name 的元素的实际尺寸/可见性列出来，样式没命中时一眼看得出来。</summary>
    private static void AuditNamedElements(Window window, StringBuilder log)
    {
        ForceCanvas(window);

        log.AppendLine();
        log.AppendLine("带名字的元素:");
        Walk(window, log);
        return;

        static void Walk(DependencyObject node, StringBuilder log)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement { Name.Length: > 0 } element)
                {
                    log.AppendLine(
                        $"  {element.Name,-18} {element.GetType().Name,-22} " +
                        $"vis={element.Visibility,-9} {element.ActualWidth:F0}x{element.ActualHeight:F0}");
                }

                Walk(child, log);
            }
        }
    }

    /// <summary>某个坐标底下压着哪些元素（截图里出现不明色块时用）。</summary>
    private static void HitTestAt(Window window, double x, double y, StringBuilder log)
    {
        if (window.Content is not FrameworkElement content) return;
        ForceCanvas(window);

        log.AppendLine();
        log.AppendLine($"命中测试 ({x},{y}) 下的元素:");
        VisualTreeHelper.HitTest(
            content,
            _ => HitTestFilterBehavior.Continue,
            result =>
            {
                var visual = result.VisualHit;
                var element = visual as FrameworkElement;
                log.AppendLine(
                    $"  {visual.GetType().Name,-24} name={element?.Name,-16} " +
                    $"vis={element?.Visibility,-9} {element?.ActualWidth ?? 0:F0}x{element?.ActualHeight ?? 0:F0}");
                return HitTestResultBehavior.Continue;
            },
            new PointHitTestParameters(new Point(x, y)));
    }

    /// <summary>把某个矩形范围内的元素全列出来（含被遮挡、不可命中的），用来定位不明色块。</summary>
    private static void FindInRect(Window window, Rect region, StringBuilder log)
    {
        if (window.Content is not FrameworkElement content) return;
        ForceCanvas(window);

        log.AppendLine();
        log.AppendLine($"落在 {region} 内的元素:");
        Walk(content, log);
        return;

        void Walk(DependencyObject node, StringBuilder log)
        {
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0)
                {
                    Rect bounds;
                    try
                    {
                        bounds = element.TransformToVisual(content)
                            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                    }
                    catch (InvalidOperationException)
                    {
                        bounds = Rect.Empty;
                    }

                    if (!bounds.IsEmpty && region.IntersectsWith(bounds))
                    {
                        var source = (element as System.Windows.Controls.Image)?.Source;
                        var extra = source is null ? "" : $" source={source.GetType().Name} {source.Width:F0}x{source.Height:F0}";
                        // IsVisible 会连祖先一起算；Visibility 只看自己，祖先 Collapsed 时子元素仍是 Visible。
                        log.AppendLine(
                            $"  {(element.IsVisible ? "VIS " : "--- ")}{element.GetType().Name,-24} name={element.Name,-16} " +
                            $"op={element.Opacity:F2} hit={element.IsHitTestVisible,-5} " +
                            $"{bounds.X:F0},{bounds.Y:F0} {bounds.Width:F0}x{bounds.Height:F0}{extra}");
                    }
                }

                Walk(child, log);
            }
        }
    }

    private static void ReportErrors(StringBuilder log)
    {
        log.AppendLine();
        log.AppendLine($"绑定/渲染警告 {Errors.Count} 条:");
        foreach (var line in Errors.Lines.Take(60)) log.AppendLine($"  {line}");
        if (Errors.Count > 60) log.AppendLine($"  … 还有 {Errors.Count - 60} 条");
    }

    /// <summary>
    /// 把内容根强行排布到固定的画布尺寸（<see cref="CanvasWidth"/>x<see cref="CanvasHeight"/>），绕开窗口管理器。
    ///
    /// <para>屏幕比窗口窄时（无头会话常见 1024x768），窗口管理器会把窗口夹到
    /// <c>虚拟屏宽 + 边框</c>（实测 1044），而且<b>事后改 <c>Window.Width</c> 也压不住</b> ——
    /// WM 会立刻夹回来。窗口一窄，内容就跟着窄，出图和 Avalonia 那份同尺寸基准对不上。</para>
    ///
    /// <para>做法：直接对内容根调 <c>Measure</c> + <c>Arrange</c>，<b>不调 <c>UpdateLayout()</c></b>
    /// —— 那会把整棵树（含 Window 自己）重排一遍，反而把强制尺寸冲掉。</para>
    ///
    /// <para>排布改完还<b>必须让渲染管线跑一轮</b>再截图，见 <see cref="Capture"/> 里的
    /// <c>Dispatcher.Yield(DispatcherPriority.Render)</c>。少了这一步，第一次截图拿到的是
    /// 上一轮排布的绘制内容，右边多出来的那 136px 会是空的。</para>
    /// </summary>
    private static void ForceCanvas(Window window, int width = CanvasWidth, int height = CanvasHeight)
    {
        if (window.Content is not FrameworkElement content) return;

        window.Width = width;
        window.Height = height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
    }

    private static async Task Capture(Window window, string fileName, StringBuilder log, int canvasWidth = CanvasWidth, int canvasHeight = CanvasHeight)
    {
        if (window.Content is not FrameworkElement content)
        {
            log.AppendLine($"{fileName}: 窗口内容不是 FrameworkElement");
            return;
        }

        ForceCanvas(window, canvasWidth, canvasHeight);

        // 排布改完之后必须让渲染管线跑一轮，再交给 RenderTargetBitmap。
        // 否则它拿到的还是**上一轮排布**留下的绘制内容 —— 表现是窗口比画布窄时，
        // 右边多出来的那一条是空的（第一次截图必然中招，之后几次才对）。
        // 这里不能用 UpdateLayout()：那会把整棵树（含 Window）重排回去，强制尺寸就白设了。
        await Dispatcher.Yield(DispatcherPriority.Render);

        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            log.AppendLine($"{fileName}: 布局尺寸 {width}x{height}，跳过");
            return;
        }
        if (width != canvasWidth || height != canvasHeight)
        {
            log.AppendLine($"{fileName}: 警告 强制画布失败，实际 {width}x{height}（应为 {canvasWidth}x{canvasHeight}）");
        }

        var path = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                // Stretch=None + 左上对齐：把内容按原尺寸画在 (0,0)，抵消父级偏移。
                var brush = new VisualBrush(content)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                };
                context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

            if (UseDirectRender)
            {
                // 直接渲染：内容根的偏移是 0，不用绕 VisualBrush。用来分辨"真元素"和"VisualBrush 伪影"。
                bitmap.Render(content);
            }
            else
            {
                bitmap.Render(visual);
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);

            log.AppendLine($"{fileName}  {width}x{height}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"{fileName}: 截图失败 {ex.Message}");
        }
    }

    /// <summary>
    /// 单独渲染一个元素。<see cref="Capture"/> 只认窗口内容根，而 <b>Popup 的内容挂在独立 HWND 上、
    /// 不在那棵渲染树里</b> —— 截窗口时浮层那块是空的，只能把它的 Child 拿出来单独拍。
    /// </summary>
    private static async Task CaptureVisual(FrameworkElement visual, string fileName, StringBuilder log)
    {
        await Dispatcher.Yield(DispatcherPriority.Render);

        var width = (int)Math.Ceiling(visual.ActualWidth);
        var height = (int)Math.Ceiling(visual.ActualHeight);
        if (width <= 0 || height <= 0)
        {
            log.AppendLine($"{fileName}: 尺寸 {width}x{height}，跳过");
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);

            log.AppendLine($"{fileName}  {width}x{height}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"{fileName}: 截图失败 {ex.Message}");
        }
    }

    /// <summary>把 UI 线程排空一轮（诊断用；截图本身不需要）。</summary>
    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>把 WPF 的绑定警告收进内存，最后写进报告。</summary>
    private sealed class BindingErrorCollector : TraceListener
    {
        private readonly List<string> _lines = [];
        private readonly StringBuilder _pending = new();

        public int Count => _lines.Count;

        public IReadOnlyList<string> Lines => _lines;

        public override void Write(string? message) => _pending.Append(message);

        public override void WriteLine(string? message)
        {
            _pending.Append(message);
            var text = _pending.ToString().Trim();
            _pending.Clear();
            if (text.Length > 0) _lines.Add(text);
        }
    }
}
