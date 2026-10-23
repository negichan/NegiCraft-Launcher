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
/// 所有会弹文件框、或接收拖放的地方（与 WPF 侧 <c>App/MainWindow.Files.cs</c> 一一对应）：
/// 背景图片、javaw.exe、整合包与资源包、游戏目录，以及窗口级的 DragOver/Drop。
///
/// <para>Avalonia 走的是 <c>StorageProvider</c>（异步），所以这些方法都是 <c>async</c>，
/// WPF 那边是同步的 <c>OpenFileDialog</c> —— 这是两份代码唯一的结构差异。</para>
/// </summary>
public partial class MainWindow
{
    private async void OnPickBackgroundClick(object? sender, RoutedEventArgs e) => await PickBackgroundAsync();

    /// <summary>
    /// 选背景图片。设置页那颗按钮和背景调节窗里那颗走的是同一个方法 ——
    /// 文件框的 owner 始终是主窗口（调节窗只是替它转发动作，不该抢这个位置）。
    /// </summary>
    private async Task PickBackgroundAsync()
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

    // ==========================================================
    // 实例页：游戏目录与整合包 —— 与 App/MainWindow.xaml.cs 逐段对照
    // ==========================================================

    /// <summary>点外面 = 取消。导入这条路一旦跑起来就在任务列表里，不必靠这张对话框盯。</summary>
    private void OnPackBackdropPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _vm?.ClosePackImportCommand.Execute(null);

    private void OnAddFolderBackdropPointerPressed(object? sender, PointerPressedEventArgs e) =>
        _vm?.CloseAddFolderCommand.Execute(null);

    /// <summary>拖拽区那格点一下 = 浏览文件，和把文件拖进窗口是同一件事（都走 <see cref="MainWindowViewModel.OfferPackFile"/>）。</summary>
    private async void OnPickPackFileClick(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择整合包",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("整合包") { Patterns = new[] { "*.mrpack", "*.zip" } },
                new FilePickerFileType("Modrinth (.mrpack)") { Patterns = new[] { "*.mrpack" } },
                new FilePickerFileType("压缩包 (.zip)") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0) _vm.OfferPackFile(files[0].Path.LocalPath);
    }

    /// <summary>导出实例：先选保存位置，再交给 VM 跑长活；任务列表里能看到进度与取消。</summary>
    private async void OnRequestExportInstance(InstanceModel instance)
    {
        if (_vm is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出整合包",
            SuggestedFileName = instance.ExportFileName,
            DefaultExtension = "mrpack",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Modrinth 整合包") { Patterns = new[] { "*.mrpack" } },
            },
        });

        if (file?.TryGetLocalPath() is { Length: > 0 } target) await _vm.ExportInstanceAsync(instance, target);
    }

    /// <summary>
    /// 选一个已经存在的游戏目录。WPF 侧用的是 <c>Microsoft.Win32.OpenFolderDialog</c>，
    /// Avalonia 这边只有存储层那套：<c>OpenFolderPickerAsync</c> 回的是 <c>IStorageFolder</c>，
    /// 取 <c>Path.LocalPath</c> 就是同一条盘上路径。
    /// </summary>
    private async void OnPickGameFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择已有的游戏目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0) _vm.OfferNewFolder(folders[0].Path.LocalPath);
    }

    private async void OnCopyFolderPath(object? sender, RoutedEventArgs e)
    {
        if (sender is not StyledElement { DataContext: GameFolderModel folder }) return;
        if (Clipboard is not { } clipboard) return;

        try
        {
            await clipboard.SetTextAsync(folder.Path);
        }
        catch (Exception)
        {
            // 剪贴板被别的进程占住是 Windows 上的常事，复制不成不值得弹一句错。
        }
    }

    /// <summary>
    ///     拖文件进窗口 = 导入整合包。
    ///
    ///     <para><b>DragOver 每动一下就来一次</b>，所以遮罩只在该亮的时候亮着写；真正判"离开了"要看
    ///     指针是不是出了窗口边界 —— 子元素之间穿来穿去也会发 DragLeave，直接熄就会一路闪。</para>
    /// </summary>
    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (_vm is not { } vm) return;

        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        vm.IsDropOverlayVisible = true;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object? sender, DragEventArgs e)
    {
        if (_vm is not { } vm) return;

        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > Bounds.Width || point.Y > Bounds.Height)
        {
            vm.IsDropOverlayVisible = false;
        }
    }

    private void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (_vm is not { } vm) return;

        vm.IsDropOverlayVisible = false;

        // 只接第一个文件：一次拖进来五个包，串行导入要排十分钟，不如让人再拖一次。
        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
        if (files[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        vm.OfferPackFile(path);
        e.Handled = true;
    }

    private async void OnRequestAddModFile()
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加模组",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Minecraft 模组 (*.jar)") { Patterns = new[] { "*.jar" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0 && !string.IsNullOrEmpty(_vm.EditingModsDirectory))
        {
            Directory.CreateDirectory(_vm.EditingModsDirectory);
            int added = 0;
            foreach (var f in files)
            {
                var src = f.Path.LocalPath;
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(_vm.EditingModsDirectory, Path.GetFileName(src));
                try
                {
                    File.Copy(src, dest, true);
                    added++;
                }
                catch { }
            }
            _vm.RefreshInstanceManageDataCommand.Execute(null);
            _vm.ShowBanner($"已添加 {added} 个模组。");
        }
    }

    private async void OnRequestAddResourcePackFile()
    {
        if (_vm is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "添加资源包",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("资源包 (*.zip)") { Patterns = new[] { "*.zip" } },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*" } },
            }
        });

        if (files.Count > 0 && !string.IsNullOrEmpty(_vm.EditingResourcePacksDirectory))
        {
            Directory.CreateDirectory(_vm.EditingResourcePacksDirectory);
            int added = 0;
            foreach (var f in files)
            {
                var src = f.Path.LocalPath;
                if (!File.Exists(src)) continue;
                var dest = Path.Combine(_vm.EditingResourcePacksDirectory, Path.GetFileName(src));
                try
                {
                    File.Copy(src, dest, true);
                    added++;
                }
                catch { }
            }
            _vm.RefreshInstanceManageDataCommand.Execute(null);
            _vm.ShowBanner($"已添加 {added} 个资源包。");
        }
    }

}
