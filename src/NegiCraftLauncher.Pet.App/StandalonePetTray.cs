using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace NegiCraftLauncher.Pet.App;

/// <summary>
/// 独立桌宠（<c>NegiPet.exe</c>）的系统托盘。桌宠是一个没有标题栏、贴在桌面上的小人，
/// 收起它、结束它都得先找到它 —— 没有托盘就只能去任务管理器杀进程。
///
/// <para>做法与启动器 <c>MainWindow</c> 的托盘逐条一致（见 <c>docs/wpf-migration-plan.md</c> §31）：
/// 图标只能靠 WinForms 的 <see cref="Forms.NotifyIcon"/>（WPF 自己没有托盘 API），
/// 但<b>菜单不交给 WinForms</b> —— <c>ContextMenuStrip</c> 画的是系统原生外观，和深色主题对不上。
/// 右键自己弹 App.xaml 里声明的那个 WPF <see cref="ContextMenu"/>，隐式样式自然命中。</para>
/// </summary>
public sealed class StandalonePetTray : IDisposable
{
    private readonly PetWindow _pet;
    private readonly Forms.NotifyIcon? _icon;
    private readonly FrameworkElement? _host;
    private readonly ContextMenu? _menu;
    private readonly MenuItem? _toggleItem;

    public StandalonePetTray(PetWindow pet)
    {
        _pet = pet;

        try
        {
            // 菜单是 PetWindow.xaml 里内联声明的那份（见那里的注释：ResourceDictionary 里的
            // ContextMenu 没有逻辑父级，会弹成透明的并且一开就自己关掉）。
            var (host, menu) = pet.TrayMenuParts;
            _host = host;
            _menu = menu;

            _toggleItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => "toggle".Equals(m.Tag));
            var settingsItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => "settings".Equals(m.Tag));
            var exitItem = menu.Items.OfType<MenuItem>().FirstOrDefault(m => "exit".Equals(m.Tag));

            // 不用 XAML 的 Click 属性：那样事件处理就得写进共用的 PetWindow.xaml.cs，
            // 而托盘只有独立版才有。
            if (_toggleItem is not null) _toggleItem.Click += (_, _) => Toggle();
            if (settingsItem is not null) settingsItem.Click += (_, _) => _pet.OpenSettingsWindow();
            if (exitItem is not null) exitItem.Click += (_, _) => Application.Current.Shutdown();

            var exe = Environment.ProcessPath;
            var icon = exe is { Length: > 0 } ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;

            _icon = new Forms.NotifyIcon
            {
                Icon = icon!,
                Text = "NegiPet 桌宠",
                Visible = true,
            };

            // 不设 ContextMenuStrip —— 让 WinForms 别接管右键，自己弹 WPF 菜单。
            _icon.MouseUp += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) ShowMenu();
            };
            _icon.MouseClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) Toggle();
            };

            SyncToggleLabel();
        }
        catch (Exception ex)
        {
            // 托盘起不来不该拖垮桌宠本身；和启动器一样只记一行。
            Console.WriteLine($"[TrayIcon] Init error: {ex.Message}");
            _icon = null;
            _menu = null;
        }
    }

    /// <summary>
    /// 在光标处弹出托盘菜单。<c>internal</c> 是给调试桥的 <c>pet-tray menu</c> 用的 ——
    /// 本机验视觉类改动一律不模拟鼠标（那会抢焦点，也测不到真实落点），所以留一个程序化入口。
    ///
    /// <para>⚠️ 不能用 <see cref="PlacementMode.MousePoint"/>：WPF 用的是它自己缓存的鼠标位置，
    /// 而那份缓存只在窗口收到鼠标消息时才更新 —— 托盘右键时桌宠窗口往往不在光标底下（甚至已经收起），
    /// 菜单就会弹到上次鼠标经过窗口的位置去。这里直接问 Win32 要光标的<b>物理像素</b>位置，
    /// 再按窗口 DPI 折成 DIP。实测弹窗矩形的左上角恰好等于光标坐标。</para>
    /// </summary>
    internal void ShowMenu()
    {
        if (_menu is null || _pet is not { IsLoaded: true }) return;

        SyncToggleLabel();

        var pos = Forms.Cursor.Position;                 // 物理屏幕像素
        var dpi = VisualTreeHelper.GetDpi(_pet);

        // PlacementTarget 必须是**有尺寸、在可视树里**的元素。
        // 实测三种给法：给 0 尺寸宿主 → 弹窗窗口建出来了、位置对、也能通过命中测试，但整块画成空的
        // （抓屏上什么都没有）；给 null → 同样画不出来；给桌宠的根 Grid → 正常。
        // 菜单本身仍然声明在那个 0 尺寸宿主上（它需要的是逻辑父级，见 PetWindow.xaml 的注释），
        // 这里只是把"继承上下文 + 合成归属"挂到真正有尺寸的根面板上。
        _menu.PlacementTarget = (FrameworkElement)_pet.Content;
        _menu.Placement = PlacementMode.AbsolutePoint;
        _menu.HorizontalOffset = pos.X / dpi.DpiScaleX;
        _menu.VerticalOffset = pos.Y / dpi.DpiScaleY;
        _menu.IsOpen = true;
    }

    /// <summary>左键托盘 / 菜单项共用：收起 ⇄ 显示。Hide 不触发 OnLastWindowClose，进程照活。</summary>
    public void Toggle()
    {
        if (_pet.IsVisible) _pet.Hide();
        else
        {
            _pet.Show();
            // 桌宠是 WS_EX_NOACTIVATE 的工具窗口，Show 之后不会自己抢前台 —— 这正是我们要的，
            // 但托盘菜单还开着的话得让它先关掉，否则点完"显示"菜单还挂在屏幕上。
            if (_menu?.IsOpen == true) _menu.IsOpen = false;
        }

        SyncToggleLabel();
    }

    private void SyncToggleLabel()
    {
        if (_toggleItem is not null) _toggleItem.Header = _pet.IsVisible ? "收起桌宠" : "显示桌宠";
    }

    /// <summary>
    /// 调试用：把托盘菜单自己渲染成位图（照启动器的 <c>shot-tray</c> 同一套做法）。
    ///
    /// <para>它和抓屏的区别就是它要回答的问题：<b>菜单内容有没有渲染出来</b>。
    /// 抓屏抓不到、这里能出图 ⇒ 内容与样式都好，问题在合成/上屏那一段；
    /// 这里也出不了图（或尺寸为 0）⇒ 是内容/样式根本没套上。</para>
    /// </summary>
    internal string ShotMenu(string path)
    {
        if (_menu is null) return "ERR no tray menu";
        if (!_menu.IsOpen) ShowMenu();

        _menu.UpdateLayout();
        var w = (int)Math.Ceiling(_menu.ActualWidth);
        var h = (int)Math.Ceiling(_menu.ActualHeight);
        if (w <= 0 || h <= 0) return $"ERR tray menu has no size ({w}x{h})";

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(_menu);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);

        return $"OK {path} w={w} h={h} items={_menu.Items.Count}";
    }

    /// <summary>调试桥 <c>pet-tray state</c> 读的自检串。托盘起不来时这里会如实报 failed。</summary>
    public string State =>
        $"icon={(_icon is null ? "failed" : _icon.Visible ? "shown" : "hidden")} " +
        $"menu={(_menu is null ? "none" : _menu.IsOpen ? "open" : "ready")} " +
        $"pet={(_pet.IsVisible ? "visible" : "hidden")} " +
        $"toggle={_toggleItem?.Header ?? "-"}";

    /// <summary>
    /// ⚠️ 必须显式 Dispose：<see cref="Forms.NotifyIcon"/> 是壳层（Explorer）持有的图标，
    /// 进程不走正常释放路径退出时图标会**残留在托盘里**，直到 Explorer 刷新。
    /// </summary>
    public void Dispose()
    {
        if (_icon is null) return;

        _icon.Visible = false;
        _icon.Dispose();
    }
}
