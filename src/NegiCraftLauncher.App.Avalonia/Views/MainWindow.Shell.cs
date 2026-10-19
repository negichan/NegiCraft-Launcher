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
/// 壳外那部分（与 WPF 侧 <c>App/MainWindow.Shell.cs</c> 一一对应）：托盘图标与它的原生菜单、
/// 桌宠窗口的开合、退出流程。
///
/// <para>Avalonia 的托盘是自带的 <c>TrayIcon</c> + <c>NativeMenu</c>，菜单画出来是系统原生的 ——
/// 不像 WPF 那边要用 WinForms 的 NotifyIcon 再自己弹 ContextMenu。代码从
/// <c>MainWindow.axaml.cs</c> 原样搬来。</para>
/// </summary>
public partial class MainWindow
{
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
            // Alt+F4 与任务栏的"关闭窗口"绕不开 OnClosing，所以策略挂在这里：这一趟一律先拦下，
            // 去向交给 VM 的 RequestClose（托盘 / 退出 / 弹询问窗）。✕ 按钮走同一个入口。
            e.Cancel = true;
            _vm?.RequestClose();
            return;
        }
        base.OnClosing(e);
    }
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
}
