using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NegiCraftLauncher.App.ViewModels;

namespace NegiCraftLauncher.App.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm is not null)
        {
            _vm.HideWindowRequested -= Hide;
            _vm.ShowWindowRequested -= Restore;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = DataContext as MainWindowViewModel;
        if (_vm is not null)
        {
            _vm.HideWindowRequested += Hide;
            _vm.ShowWindowRequested += Restore;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

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
        Close();
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

    private void OnDialogBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking outside cancels rather than confirms, so a dialog can never be committed by accident.
        _vm?.CancelDialogCommand.Execute(null);
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