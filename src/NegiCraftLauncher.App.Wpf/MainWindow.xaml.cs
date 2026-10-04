using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NegiCraftLauncher.Pet.Wpf;
using NegiCraftLauncher.ViewModels;
using Forms = System.Windows.Forms;

namespace NegiCraftLauncher.App.Wpf;

/// <summary>
/// 主窗口。逐条对应 Avalonia 侧 <c>App/Views/MainWindow.axaml.cs</c> 的事件处理，
/// 差别只在平台 API：
/// <list type="bullet">
/// <item><c>BeginMoveDrag</c> → <see cref="Window.DragMove"/>（必须按着左键调）。</item>
/// <item><c>StorageProvider.OpenFilePickerAsync</c> → <see cref="Microsoft.Win32.OpenFileDialog"/>。</item>
/// <item><c>TrayIcon</c>（Avalonia 自带）→ WinForms 的 <see cref="Forms.NotifyIcon"/>，WPF 没有托盘 API。</item>
/// </list>
/// </summary>
public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;
    private PetWindow? _petWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _petTrayMenuItem;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
        SetupTrayIcon();
        DataContextChanged += OnDataContextChangedHandler;
    }

    /// <summary>调试桥通过它拿桌宠窗口（可以关着，所以可空）。</summary>
    public PetWindow? PetWindowInstance => _petWindow;

    /// <summary>
    /// WPF 的 <c>Border</c> 即使 <c>ClipToBounds=True</c> 也只裁矩形，圆角得自己给一条裁剪几何，
    /// 否则满幅的首页背景会在四个角上露出直角。
    /// </summary>
    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Shell.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 22, 22);
    }

    // ==========================================================
    // 托盘
    // ==========================================================

    private void SetupTrayIcon()
    {
        try
        {
            var menu = new Forms.ContextMenuStrip();

            var openLauncher = new Forms.ToolStripMenuItem("打开启动器");
            openLauncher.Click += (_, _) => Restore();
            menu.Items.Add(openLauncher);

            _petTrayMenuItem = new Forms.ToolStripMenuItem("桌面宠物");
            _petTrayMenuItem.Click += (_, _) => TogglePetWindow();
            menu.Items.Add(_petTrayMenuItem);

            menu.Items.Add(new Forms.ToolStripSeparator());

            var exit = new Forms.ToolStripMenuItem("退出启动器");
            exit.Click += (_, _) => ExitApplication();
            menu.Items.Add(exit);

            var icon = Environment.ProcessPath is { Length: > 0 } exe
                ? System.Drawing.Icon.ExtractAssociatedIcon(exe)
                : null;

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = icon,
                Text = "NegiCraft Launcher",
                Visible = true,
                ContextMenuStrip = menu,
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

    public void ExitApplication()
    {
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

    protected override void OnClosing(CancelEventArgs e)
    {
        // 点关闭只是收进托盘；真正退出走 ExitApplication。
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

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
            _vm.OpenPetRequested -= OnOpenPetRequested;
            _vm.RecallPetRequested -= ClosePetWindow;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.ThemeChanged -= OnThemeChanged;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.HideWindowRequested += Hide;
            _vm.ShowWindowRequested += Restore;
            _vm.OpenPetRequested += OnOpenPetRequested;
            _vm.RecallPetRequested += ClosePetWindow;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.ThemeChanged += OnThemeChanged;
        }
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
            _petTrayMenuItem.Text = _petWindow is { IsVisible: true } ? "收起桌宠" : "桌面宠物";
        }
    }

    /// <summary>
    /// 调试用：程序化弹出托盘右键菜单。Avalonia 那边得靠反射调 <c>TrayIcon._impl.OnRightClicked</c>，
    /// WPF 的托盘就是 WinForms 的 <see cref="Forms.NotifyIcon"/>，直接 Show 即可。
    /// </summary>
    internal bool ShowTrayMenuForDebug()
    {
        var menu = _trayIcon?.ContextMenuStrip;
        if (menu is null) return false;
        menu.Show(Forms.Cursor.Position);
        return true;
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

    // ==========================================================
    // 对话框输入
    // ==========================================================

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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
    // 窗口行为
    // ==========================================================

    private void Restore()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // DragMove 会一路阻塞到松开左键；拖动中抛异常会被 WPF 吞掉，所以包一层。
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnPopoverBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseAllPopoversCommand.Execute(null);

    private void OnBgPopBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseBgPopCommand.Execute(null);

    private void OnDialogBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        // 点外面是取消而不是确认，对话框就不可能被误提交。
        _vm?.CancelDialogCommand.Execute(null);

    private void OnInstConfigBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseInstanceConfigCommand.Execute(null);

    // ==========================================================
    // 文件选择
    // ==========================================================

    private void OnPickBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetBackgroundCommand.Execute(dialog.FileName);
    }

    private void OnPickJavaClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 javaw.exe",
            Filter = "Java 运行时|javaw.exe;java.exe|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetJavaPathCommand.Execute(dialog.FileName);
    }

    private void OnPickInstJavaClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 javaw.exe",
            Filter = "Java 运行时|javaw.exe;java.exe|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetInstJavaPathCommand.Execute(dialog.FileName);
    }

    // ==========================================================
    // Shift 蹲下
    // ==========================================================

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SkinPreview.Sneaking = true;
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) SkinPreview.Sneaking = false;
    }
}
