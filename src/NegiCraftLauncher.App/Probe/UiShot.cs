using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
/// <para><b>画布尺寸与窗口解耦</b>：屏幕比 1180 窄时窗口管理器会把窗口夹小，
/// 但截图统一强制到 1180x720（见 <see cref="ForceCanvas"/>），所以像素对照有稳定基准。</para>
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

    /// <summary>像素对照用的固定画布尺寸，与 Avalonia 版 <c>MainWindow.axaml</c> 的 Width/Height 一致。</summary>
    private const int CanvasWidth = 1180;

    private const int CanvasHeight = 720;

    private static readonly string[] SettingsTabs = ["游戏", "Java", "下载", "外观", "关于"];

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
        log.AppendLine("对照基准: Avalonia 版 design/_smoke.ps1 出的 %TEMP%\\ncl-smoke-<page>.png（1180x720，同为直接渲染）");
        log.AppendLine("像素对照: python design/_diff.py <wpf.png> <smoke.png> --shift 6");
        log.AppendLine("（design/ 下的脚本是本地私有工具，不随源码分发 —— 见 design/README.md）");

        MainWindowViewModel vm;
        try
        {
            vm = new MainWindowViewModel();
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
        await Task.Delay(3000);

        // 窗口尺寸被 WM 夹住时（屏幕比 1180 窄）不要紧 —— 截图走 ForceCanvas，
        // 把内容根强行排到 1180x720，和窗口大小解耦。
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

        // 首页右上角有一块不明亮斑，查一下那底下到底是什么元素。
        vm.GoCommand.Execute("home");
        await Task.Delay(400);
        HitTestAt(window, 1046, 65, log);
        FindInRect(window, new Rect(1000, 40, 80, 70), log);

        vm.GoCommand.Execute("settings");
        foreach (var tab in SettingsTabs)
        {
            vm.SelectSettingsTabCommand.Execute(tab);
            await Task.Delay(320);
            await Capture(window, $"ncl-wpf-settings-{tab}.png", log);
        }

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

        File.WriteAllText(ReportPath, log.ToString());
        Application.Current.Shutdown();
    }

    /// <summary>四个页面容器同一时刻只该有一个是 Visible。</summary>
    private static void AuditPages(Window window, string currentPage, StringBuilder log)
    {
        var visible = new List<string>();
        foreach (var name in PageContainerNames)
        {
            if (window.FindName(name) is not FrameworkElement element) continue;
            if (element.Visibility == Visibility.Visible) visible.Add(name);
        }

        var flag = visible.Count == 1 ? "OK" : "!!";
        log.AppendLine($"  {flag} page={currentPage}  可见容器: {string.Join(", ", visible)}");
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
    /// 把内容根强行排布到固定的 1180x720，绕开窗口管理器。
    ///
    /// <para>屏幕比 1180 窄时（无头会话常见 1024x768），窗口管理器会把窗口夹到
    /// <c>虚拟屏宽 + 边框</c>（实测 1044），而且<b>事后改 <c>Window.Width</c> 也压不住</b> ——
    /// WM 会立刻夹回来。窗口一窄，内容就跟着窄，出图和 Avalonia 的 1180x720 基准对不上。</para>
    ///
    /// <para>做法：直接对内容根调 <c>Measure</c> + <c>Arrange</c>，<b>不调 <c>UpdateLayout()</c></b>
    /// —— 那会把整棵树（含 Window 自己）重排一遍，反而把强制尺寸冲掉。</para>
    ///
    /// <para>排布改完还<b>必须让渲染管线跑一轮</b>再截图，见 <see cref="Capture"/> 里的
    /// <c>Dispatcher.Yield(DispatcherPriority.Render)</c>。少了这一步，第一次截图拿到的是
    /// 上一轮排布的绘制内容，右边多出来的那 136px 会是空的。</para>
    /// </summary>
    private static void ForceCanvas(Window window)
    {
        if (window.Content is not FrameworkElement content) return;

        content.Measure(new Size(CanvasWidth, CanvasHeight));
        content.Arrange(new Rect(0, 0, CanvasWidth, CanvasHeight));
    }

    private static async Task Capture(Window window, string fileName, StringBuilder log)
    {
        if (window.Content is not FrameworkElement content)
        {
            log.AppendLine($"{fileName}: 窗口内容不是 FrameworkElement");
            return;
        }

        ForceCanvas(window);

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
        if (width != CanvasWidth || height != CanvasHeight)
        {
            log.AppendLine($"{fileName}: 警告 强制画布失败，实际 {width}x{height}（应为 {CanvasWidth}x{CanvasHeight}）");
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
