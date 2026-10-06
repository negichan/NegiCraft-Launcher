using System;
using System.Windows;
using System.Windows.Input;
using NegiCraftLauncher.App.Services;

namespace NegiCraftLauncher.App.Views;

/// <summary>
/// 背景调节窗口：主窗口外面的一扇小工具窗。
///
/// <para>它<b>只读不写业务</b> —— 滑杆全部直接绑在共享的 <c>MainWindowViewModel</c> 上，
/// 所以这扇窗开着还是关着，背景该变都变；文件选择框和 Wallpaper Engine 同步仍然归主窗口
/// （那两个都要以主窗口为 owner 弹，而且逻辑本来就在它那儿），这里只把请求转出去。</para>
///
/// <para>开关时机、贴哪一边、位置存盘，全由主窗口管 —— 见
/// <c>MainWindow</c> 里的 <c>SyncBackgroundTuningWindow</c>。</para>
/// </summary>
public sealed partial class BackgroundTuningWindow : Window
{
    /// <summary>请求弹"选择背景图片"。文件框归主窗口弹（owner 得是主窗口）。</summary>
    public event Action? PickBackgroundRequested;

    /// <summary>请求弹"选择背景视频"。</summary>
    public event Action? PickVideoRequested;

    /// <summary>请求从 Wallpaper Engine 同步一张过来。</summary>
    public event Action? SyncWallpaperEngineRequested;

    /// <summary>用户把这扇窗拖完了，上报新的屏幕位置（DIP）让主窗口存下来。</summary>
    public event Action<double, double>? Moved;

    public BackgroundTuningWindow()
    {
        InitializeComponent();

        // 不进任务栏也不进 Alt+Tab：它是主窗口的附属小窗，跟着主窗口收放。
        ToolWindowStyle.Apply(this);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        // DragMove 会自己跑到鼠标松开才回来（WPF 内置的那套模态拖动），所以它返回时
        // Left/Top 就是最终位置 —— 正好是"拖完了"这个时机。
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 非左键 / 已经不在按下状态：WPF 会抛，忽略即可，不该让标题栏点一下崩一次。
            return;
        }

        Moved?.Invoke(Left, Top);
    }

    private void OnPickImageClick(object sender, RoutedEventArgs e) => PickBackgroundRequested?.Invoke();

    private void OnPickVideoClick(object sender, RoutedEventArgs e) => PickVideoRequested?.Invoke();

    private void OnSyncWallpaperEngineClick(object sender, RoutedEventArgs e) => SyncWallpaperEngineRequested?.Invoke();
}
