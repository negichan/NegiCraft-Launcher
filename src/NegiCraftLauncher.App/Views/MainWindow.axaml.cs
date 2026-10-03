using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NegiCraftLauncher.ViewModels;
using NegiCraftLauncher.Pet;

namespace NegiCraftLauncher.App.Views;

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
    }

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
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://NegiCraftLauncher.App/Assets/app.ico"))),
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
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.HideWindowRequested += Hide;
            _vm.ShowWindowRequested += Restore;
            _vm.OpenPetRequested += OnOpenPetRequested;
            _vm.RecallPetRequested += ClosePetWindow;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
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

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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

    private void OnBgPopBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseBgPopCommand.Execute(null);
    }

    private async void OnPickBackgroundClick(object? sender, RoutedEventArgs e)
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