using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NegiCraftLauncher.App.Probe;

/// <summary>
/// P1 风险探针。以 <c>--selftest</c> 启动时无头跑完，结果写到
/// <c>%TEMP%\ncl-wpf-selftest.txt</c>，然后进程退出。
///
/// <para>四项都是"只凭记忆判断会出错"的地方，必须在真机上实测一次：</para>
/// <list type="number">
/// <item><c>AllowsTransparency=True</c> + <c>WindowStyle=None</c> 能否逐像素 alpha；</item>
/// <item>注入 <c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c> 后样式是否真的落上；</item>
/// <item>不可激活窗口上 <c>ContextMenu</c> / <c>Popup</c> 还能不能打开（登记在案的高危项）；</item>
/// <item>透明分层窗里放 <c>HwndHost</c> 是否真的不显示（决定 §5.1 的硬约束成不成立）。</item>
/// </list>
/// </summary>
internal static class SelfTest
{
    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "ncl-wpf-selftest.txt");

    public static void Run()
    {
        var log = new StringBuilder();
        log.AppendLine("NegiCraft Launcher — WPF 风险探针");
        log.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine();

        var frame = new DispatcherFrame();
        _ = RunAsync(log, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);

        File.WriteAllText(ResultPath, log.ToString());
    }

    private static async Task RunAsync(StringBuilder log, Action done)
    {
        try
        {
            await ProbeLayeredAlphaAsync(log);
            await ProbeToolWindowAsync(log);
            await ProbePopupOnNoActivateAsync(log);
            await ProbeHwndHostAsync(log);
        }
        catch (Exception ex)
        {
            log.AppendLine($"[异常] {ex}");
        }
        finally
        {
            log.AppendLine();
            log.AppendLine($"结果文件: {ResultPath}");
            done();
        }
    }

    // ============================================================
    // 探针 1：分层透明窗的逐像素 alpha
    // ============================================================

    private static async Task ProbeLayeredAlphaAsync(StringBuilder log)
    {
        log.AppendLine("== 探针 1：AllowsTransparency=True + WindowStyle=None 的逐像素 alpha ==");

        var red = new Border { Background = Brushes.Red };                       // 左半：不透明红
        var semi = new Border                                                    // 右下：半透明蓝
        {
            Width = 60,
            Height = 60,
            Background = Brushes.Blue,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20),
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(red, 0);
        grid.Children.Add(red);
        Grid.SetColumn(semi, 1);
        grid.Children.Add(semi);

        var window = CreateTransparentWindow("NCL 探针 1", 120, 120, 320, 220);
        window.Content = grid;

        // 先建 HWND（不显示）拿到物理像素矩形，好把"显示前"的桌面底图截下来做对照。
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Win32.GetWindowRect(hwnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;

        if (w <= 0 || h <= 0)
        {
            log.AppendLine("  [跳过] EnsureHandle 之后拿不到窗口矩形。");
            window.Close();
            return;
        }

        var before = Win32.CaptureScreen(rect.Left, rect.Top, w, h);

        window.Show();
        await SettleAsync();

        var after = Win32.CaptureScreen(rect.Left, rect.Top, w, h);
        var exStyle = (uint)Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);

        var redPx = Win32.At(after, w, w / 4, h / 2);
        var clearPx = Win32.At(after, w, w * 3 / 4, h / 4);
        var clearBefore = Win32.At(before, w, w * 3 / 4, h / 4);
        var semiPx = Win32.At(after, w, w * 3 / 4, h * 3 / 4);
        var semiBefore = Win32.At(before, w, w * 3 / 4, h * 3 / 4);

        log.AppendLine($"  物理矩形: {w}x{h} @ ({rect.Left},{rect.Top})");
        log.AppendLine($"  ex-style: 0x{exStyle:X8}  WS_EX_LAYERED={(exStyle & Win32.WS_EX_LAYERED) != 0}");
        log.AppendLine($"  不透明红区   取色 0x{redPx:X6}  (期望 ≈ 0xFF0000)");
        log.AppendLine($"  全透明区     取色 0x{clearPx:X6}  / 显示前桌面 0x{clearBefore:X6}");
        log.AppendLine($"  半透明蓝区   取色 0x{semiPx:X6}  / 显示前桌面 0x{semiBefore:X6}");

        var redOk = IsClose(redPx, 0xFF0000, 12);
        var clearOk = IsClose(clearPx, clearBefore, 8);
        log.AppendLine($"  结论: 不透明区正确={redOk}，全透明区透出桌面={clearOk}");

        window.Close();
        await SettleAsync(120);
        log.AppendLine();
    }

    // ============================================================
    // 探针 2：TOOLWINDOW / NOACTIVATE 注入
    // ============================================================

    private static async Task ProbeToolWindowAsync(StringBuilder log)
    {
        log.AppendLine("== 探针 2：WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE 注入 ==");

        var window = CreateTransparentWindow("NCL 探针 2", 120, 360, 240, 160);
        window.Content = new Border { Background = new SolidColorBrush(Color.FromArgb(200, 30, 30, 40)) };

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        window.Show();
        await SettleAsync();

        var before = (uint)Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        var injected = before | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, unchecked((int)injected));
        await SettleAsync();

        var after = (uint)Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        var foreground = Win32.GetForegroundWindow();

        log.AppendLine($"  注入前 ex-style: 0x{before:X8}");
        log.AppendLine($"  注入后 ex-style: 0x{after:X8}");
        log.AppendLine($"  TOOLWINDOW={(after & Win32.WS_EX_TOOLWINDOW) != 0}  " +
                       $"NOACTIVATE={(after & Win32.WS_EX_NOACTIVATE) != 0}  " +
                       $"LAYERED={(after & Win32.WS_EX_LAYERED) != 0}  " +
                       $"TOPMOST={(after & Win32.WS_EX_TOPMOST) != 0}");
        log.AppendLine($"  仍然可见: {Win32.IsWindowVisible(hwnd)}");
        log.AppendLine($"  注入后前台窗口是否本窗口: {foreground == hwnd}（期望 False）");

        window.Close();
        await SettleAsync(120);
        log.AppendLine();
    }

    // ============================================================
    // 探针 3：NOACTIVATE 窗口上的 ContextMenu / Popup（高危）
    // ============================================================

    private static async Task ProbePopupOnNoActivateAsync(StringBuilder log)
    {
        log.AppendLine("== 探针 3：NOACTIVATE 窗口上的 Popup / ContextMenu ==");

        var anchor = new Border
        {
            Width = 120,
            Height = 40,
            Background = Brushes.SteelBlue,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var window = CreateTransparentWindow("NCL 探针 3", 400, 360, 300, 200);
        window.Content = anchor;

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        window.Show();
        await SettleAsync();

        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            unchecked((int)((uint)Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE)
                            | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE)));
        await SettleAsync();

        var foregroundBefore = Win32.GetForegroundWindow();

        // 3a. 裸 Popup
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Center,
            Child = new Border
            {
                Width = 140,
                Height = 70,
                Background = Brushes.Orange,
                Child = new TextBlock { Text = "popup", HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
        popup.IsOpen = true;
        await SettleAsync();

        var popupSource = PresentationSource.FromVisual(popup.Child) as HwndSource;
        var popupHwnd = popupSource?.Handle ?? IntPtr.Zero;
        var popupVisible = popupHwnd != IntPtr.Zero && Win32.IsWindowVisible(popupHwnd);
        var popupRectOk = false;
        if (popupHwnd != IntPtr.Zero && Win32.GetWindowRect(popupHwnd, out var pr))
        {
            popupRectOk = pr.Right - pr.Left > 0 && pr.Bottom - pr.Top > 0;
        }

        log.AppendLine($"  Popup  IsOpen={popup.IsOpen}  hwnd=0x{popupHwnd:X}  " +
                       $"可见={popupVisible}  有尺寸={popupRectOk}");
        popup.IsOpen = false;
        await SettleAsync(120);

        // 3b. ContextMenu（右键菜单走的是另一条路）
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "自由待机" });
        menu.Items.Add(new MenuItem { Header = "鼠标跟随" });
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
        await SettleAsync();

        var menuSource = PresentationSource.FromVisual(menu) as HwndSource;
        var menuHwnd = menuSource?.Handle ?? IntPtr.Zero;
        var menuVisible = menuHwnd != IntPtr.Zero && Win32.IsWindowVisible(menuHwnd);

        log.AppendLine($"  ContextMenu  IsOpen={menu.IsOpen}  hwnd=0x{menuHwnd:X}  可见={menuVisible}");
        log.AppendLine($"  打开弹窗后前台窗口是否被抢: {Win32.GetForegroundWindow() != foregroundBefore}（期望 False）");

        menu.IsOpen = false;
        window.Close();
        await SettleAsync(120);
        log.AppendLine();
    }

    // ============================================================
    // 探针 4：透明分层窗里的 HwndHost
    // ============================================================

    private static async Task ProbeHwndHostAsync(StringBuilder log)
    {
        log.AppendLine("== 探针 4：HwndHost 在普通窗 vs 透明分层窗里 ==");

        // 4a. 对照组：普通不透明窗。如果这里都看不到绿色，说明是探针本身写错了。
        var opaqueGreen = await RunHwndHostCaseAsync(layered: false, x: 760, y: 120);
        log.AppendLine($"  对照组（普通窗，非分层）: 中心取色 0x{opaqueGreen:X6}  " +
                       $"{(IsClose(opaqueGreen, 0x00FF00, 40) ? "→ 绿色，HwndHost 正常工作" : "→ 不是绿色，探针本身有问题")}");

        // 4b. 目标：透明分层窗
        var layeredPx = await RunHwndHostCaseAsync(layered: true, x: 760, y: 360);
        log.AppendLine($"  目标组（透明分层窗）:     中心取色 0x{layeredPx:X6}  " +
                       $"{(IsClose(layeredPx, 0x00FF00, 40) ? "→ 绿色，§5.1 结论被推翻" : "→ 非绿色，确认子 HWND 不显示")}");

        log.AppendLine();
    }

    private static async Task<uint> RunHwndHostCaseAsync(bool layered, double x, double y)
    {
        var window = new Window
        {
            Title = layered ? "NCL 探针 4b" : "NCL 探针 4a",
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = x,
            Top = y,
            Width = 260,
            Height = 180,
            ShowInTaskbar = false,
            Topmost = true,
        };

        if (layered)
        {
            window.WindowStyle = WindowStyle.None;
            window.AllowsTransparency = true;
            window.Background = Brushes.Transparent;
        }

        var host = new ProbeHwndHost { Width = 160, Height = 110 };
        window.Content = new Border
        {
            Background = layered ? Brushes.Transparent : Brushes.DarkSlateGray,
            Child = host,
        };

        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        window.Show();
        await SettleAsync();

        Win32.GetWindowRect(hwnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        var shot = Win32.CaptureScreen(rect.Left, rect.Top, Math.Max(1, w), Math.Max(1, h));
        var center = Win32.At(shot, Math.Max(1, w), Math.Max(1, w) / 2, Math.Max(1, h) / 2);

        window.Close();
        await SettleAsync(120);
        return center;
    }

    // ============================================================
    // 辅助
    // ============================================================

    private static Window CreateTransparentWindow(string title, double x, double y, double w, double h) => new()
    {
        Title = title,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = x,
        Top = y,
        Width = w,
        Height = h,
        WindowStyle = WindowStyle.None,
        AllowsTransparency = true,
        Background = Brushes.Transparent,
        ShowInTaskbar = false,
        Topmost = true,
        ResizeMode = ResizeMode.NoResize,
    };

    /// <summary>把消息泵转几圈，让布局、渲染、弹窗都真正落地。</summary>
    private static async Task SettleAsync(int milliseconds = 350)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static bool IsClose(uint a, uint b, int tolerance)
    {
        var dr = Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
        var dg = Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF));
        var db = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF));
        return dr <= tolerance && dg <= tolerance && db <= tolerance;
    }
}
