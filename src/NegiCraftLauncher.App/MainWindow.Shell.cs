using System.ComponentModel;
using System.Diagnostics;
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
/// 主窗口的「壳外那部分」：托盘图标与它的右键菜单、桌宠窗口的开合、退出流程。
///
/// <para>拆出来是因为这些代码只在「窗口之外」起作用 —— 托盘是 WinForms 的 NotifyIcon，
/// 桌宠是另一个 Window。把它们和主窗口的布局代码挤在同一个文件里，改托盘要翻 2000 行。
/// 从 <c>MainWindow.xaml.cs</c> 原样搬来，一个字符都没改。</para>
/// </summary>
public partial class MainWindow
{
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
        // 幂等：OnClosing 排队进来的 RequestClose 和托盘"退出"可能前后脚到，
        // 第二趟再跑一遍 Close/Shutdown 只会对着已关闭的窗口抛异常。
        if (_isExplicitExit) return;
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

    /// <summary>
    /// 设置里点了「立即重启」：先拉起一份新进程，成功了才退出当前这份。
    /// 新进程起不来就留在原地报个信，不能把用户唯一还开着的窗口关掉。
    /// </summary>
    public void RestartApplication()
    {
        if (_isExplicitExit) return;

        if (!TryStartNewInstance())
        {
            _vm?.ShowBanner("没能启动新的启动器进程，请手动重启。");
            return;
        }

        ExitApplication();
    }

    private static bool TryStartNewInstance()
    {
        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host)) return false;

        try
        {
            var start = new ProcessStartInfo(host)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };

            // 从 `dotnet app.dll` 起的时候宿主是 dotnet.exe，入口 dll 得自己补回去。
            var hostedByDotnet = string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet",
                StringComparison.OrdinalIgnoreCase);
            if (hostedByDotnet)
            {
                var entry = typeof(App).Assembly.Location;
                if (string.IsNullOrEmpty(entry)) return false;
                start.ArgumentList.Add(entry);
            }

            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                // --performance 是调试用的强制开关，会把用户刚改掉的设置又压回去。
                if (arg.Equals("--performance", StringComparison.OrdinalIgnoreCase)) continue;
                // dotnet 托管时命令行里可能还夹着入口 dll，上面已经补过就不重复。
                if (hostedByDotnet && arg.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                start.ArgumentList.Add(arg);
            }

            Process.Start(start);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 点关闭只是收进托盘；真正退出走 ExitApplication。
        if (!_isExplicitExit)
        {
            // Alt+F4 和任务栏缩略图上那扇"关闭窗口"绕不开 OnClosing，所以策略挂在这里：
            // 这一趟一律先拦下，去向交给 VM 的 RequestClose（托盘 / 退出 / 弹询问窗）。
            // ✕ 按钮也走同一个入口，两条路不会分出两种行为。
            e.Cancel = true;
            // ⚠️ 必须推迟到 Closing 走完之后再问策略：RequestClose 可能直接触发
            // ExitApplication → Close()，而此时窗口还在 Closing 流程里（Cancel 只标记了这一趟），
            // 重入 Close 会让 WPF 抛 "在窗口关闭期间…无法调用 Close"（VerifyNotClosing）。
            // 没桌宠时"直接退出"这条新策略正好每次都走那段路，所以这里必须排队。
            Dispatcher.BeginInvoke(() => _vm?.RequestClose(), DispatcherPriority.Normal);
            return;
        }

        // 真正退出才释放：收进托盘那条路只是 Hide，窗口还要复用。
        // 不 Close 掉 MediaPlayer 会留一个解码线程不放。
        _backgroundVideoPreparation?.Cancel();
        _videoBackground.Dispose();
        HoverReveal.SetSuspended(VolumePanel, true);

        base.OnClosing(e);
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

}
