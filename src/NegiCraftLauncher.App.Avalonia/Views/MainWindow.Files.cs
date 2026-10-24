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
    ///     拖文件进窗口：管理页里 .jar 是加模组、资源包页里 .zip 是加资源包，其余才是导整合包。
    ///
    ///     <para><b>DragOver 每动一下就来一次</b>，所以遮罩只在该亮的时候亮着写；真正判"离开了"要看
    ///     指针是不是出了窗口边界 —— 子元素之间穿来穿去也会发 DragLeave，直接熄就会一路闪。
    ///     文案只在遮罩从暗转亮那一次算，不跟着鼠标每个 move 算一遍。</para>
    /// </summary>
    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (_vm is not { } vm) return;

        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        if (!vm.IsDropOverlayVisible) UpdateDropOverlayText(vm, files);

        vm.IsDropOverlayVisible = true;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    /// <summary>拖的是什么、在哪一页，决定遮罩上那句「松手会发生什么」。</summary>
    private static void UpdateDropOverlayText(MainWindowViewModel vm, IStorageItem[] files)
    {
        var paths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).ToArray();

        if (vm.CurrentPage == "instance_manage")
        {
            if (paths.Any(p => p!.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
            {
                vm.DropOverlayText = "释放以添加模组";
                vm.DropOverlaySubtext = "复制进当前实例的 mods 文件夹";
                return;
            }
            if (vm.ConfigTab == "资源包" && paths.Any(p => p!.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            {
                vm.DropOverlayText = "释放以添加资源包";
                vm.DropOverlaySubtext = "复制进当前实例的 resourcepacks 文件夹";
                return;
            }
        }

        vm.DropOverlayText = "释放以导入整合包";
        vm.DropOverlaySubtext = $"导入到当前文件夹：{vm.ActiveFolderName}";
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

        if (e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;

        var paths = files.Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToArray();
        if (paths.Length == 0) return;

        // 管理页里拖 .jar /（资源包页）.zip 是往实例里加东西，不走整合包导入。
        if (vm.CurrentPage == "instance_manage")
        {
            var jars = paths.Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (jars.Length > 0)
            {
                vm.AddModFilesFromDrop(jars);
                e.Handled = true;
                return;
            }

            if (vm.ConfigTab == "资源包")
            {
                var zips = paths.Where(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (zips.Length > 0)
                {
                    vm.AddResourcePackFilesFromDrop(zips);
                    e.Handled = true;
                    return;
                }
            }
        }

        // 只接第一个文件：一次拖进来五个包，串行导入要排十分钟，不如让人再拖一次。
        vm.OfferPackFile(paths[0]);
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

        if (files.Count > 0) _vm.AddModFilesFromDrop(files.Select(f => f.Path.LocalPath));
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

        if (files.Count > 0) _vm.AddResourcePackFilesFromDrop(files.Select(f => f.Path.LocalPath));
    }

}
