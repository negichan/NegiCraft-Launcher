using System.ComponentModel;
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
/// 主窗口里所有会弹文件框、或接收拖放的地方：背景图片/视频、javaw.exe、
/// 整合包与资源包、游戏目录，以及窗口级的 DragOver/Drop。
///
/// <para>统一放一处的好处是「允许拖什么」只在一个文件里判断，不用满项目找
/// <c>AllowDrop</c>。从 <c>MainWindow.xaml.cs</c> 原样搬来。</para>
/// </summary>
public partial class MainWindow
{
    // ==========================================================
    // 文件选择
    // ==========================================================

    /// <summary>
    /// 选背景图片。设置页那颗按钮和背景调节窗里那颗走的是同一个方法 ——
    /// 文件框的 owner 始终是主窗口（调节窗只是替它转发动作，不该抢这个位置）。
    /// </summary>
    /// <remarks>internal：那颗按钮挂在 <c>Styles/LauncherStyles.xaml</c> 的设置卡片模板里，
    /// 由那边的 code-behind 转发进来（字典自己不知道 VM 怎么调）。</remarks>
    internal void PickBackground()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetBackgroundCommand.Execute(dialog.FileName);
    }

    /// <summary>选视频背景。同上，两个入口共用。</summary>
    internal void PickVideo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择视频背景",
            Filter = "视频|*.mp4;*.webm;*.avi;*.mkv;*.mov;*.m4v;*.wmv|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetVideoBackgroundCommand.Execute(dialog.FileName);
    }

    /// <summary>
    /// 选 javaw.exe。<b>设置页</b>那颗「浏览 javaw.exe…」挂在样式字典的模板里，由
    /// <c>LauncherStyles.xaml.cs</c> 转发到这里；实例页那颗走 <see cref="OnPickInstJavaClick"/>，
    /// 落的是另一个 VM 命令（实例级覆盖，不是全局设置）。
    /// </summary>
    internal void PickJava()
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

    /// <summary>
    /// 实例配置「概览」页那颗「更换…」：挑一张 PNG，交给 VM 重编码成 80x80 写进实例目录。
    /// 只收 PNG —— 共享层只有 <see cref="PngCodec"/>，别的格式读不出来（VM 那边也会兜住）。
    /// </summary>
    private void OnPickInstanceIconClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择实例图标",
            Filter = "PNG 图片|*.png|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.SetInstanceIconCommand.Execute(dialog.FileName);
    }

    private void OnRequestAddModFile()
    {
        var vm = _vm;
        if (vm is null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要添加的模组 (.jar)",
            Filter = "Minecraft 模组 (*.jar)|*.jar|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
        {
            var targetDir = vm.EditingModsDirectory;
            try
            {
                Directory.CreateDirectory(targetDir);
                foreach (var file in dlg.FileNames)
                {
                    var dest = Path.Combine(targetDir, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }
                vm.RefreshInstanceManageDataCommand.Execute(null);
                vm.ShowBanner($"已添加 {dlg.FileNames.Length} 个模组。");
            }
            catch (Exception ex)
            {
                vm.ShowBanner($"添加模组失败：{ex.Message}");
            }
        }
    }

    private void OnRequestAddResourcePackFile()
    {
        var vm = _vm;
        if (vm is null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要添加的资源包 (.zip)",
            Filter = "Minecraft 资源包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
        {
            var targetDir = vm.EditingResourcePacksDirectory;
            try
            {
                Directory.CreateDirectory(targetDir);
                foreach (var file in dlg.FileNames)
                {
                    var dest = Path.Combine(targetDir, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }
                vm.RefreshInstanceManageDataCommand.Execute(null);
                vm.ShowBanner($"已添加 {dlg.FileNames.Length} 个资源包。");
            }
            catch (Exception ex)
            {
                vm.ShowBanner($"添加资源包失败：{ex.Message}");
            }
        }
    }

    // ==========================================================
    // 实例页：游戏目录与整合包
    // ==========================================================

    /// <summary>点外面 = 取消。导入这条路一旦跑起来就在任务列表里，不必靠这张对话框盯。</summary>
    private void OnPackBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.ClosePackImportCommand.Execute(null);

    private void OnAddFolderBackdropMouseDown(object sender, MouseButtonEventArgs e) =>
        _vm?.CloseAddFolderCommand.Execute(null);

    private void OnPickPackFileClick(object sender, MouseButtonEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择整合包",
            Filter = "整合包|*.mrpack;*.zip|Modrinth (.mrpack)|*.mrpack|压缩包 (.zip)|*.zip|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true) _vm?.OfferPackFile(dialog.FileName);
    }

    /// <summary>.NET 8 起 WPF 自己有目录选择框，不必再拼 SHBrowseForFolder。</summary>
    private void OnPickGameFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择已有的游戏目录" };
        if (dialog.ShowDialog(this) == true) _vm?.OfferNewFolder(dialog.FolderName);
    }

    private void OnCopyFolderPath(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameFolderModel folder }) return;

        try
        {
            Clipboard.SetText(folder.Path);
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
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        if (_vm is not { } vm) return;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        vm.IsDropOverlayVisible = true;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnWindowDragLeave(object sender, DragEventArgs e)
    {
        if (_vm is not { } vm) return;

        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
        {
            vm.IsDropOverlayVisible = false;
        }
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            return;
        }
        if (_vm is not { } vm) return;

        vm.IsDropOverlayVisible = false;

        // 只接第一个文件：一次拖进来五个包，串行导入要排十分钟，不如让人再拖一次。
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;

        vm.OfferPackFile(files[0]);
        e.Handled = true;
    }

}
