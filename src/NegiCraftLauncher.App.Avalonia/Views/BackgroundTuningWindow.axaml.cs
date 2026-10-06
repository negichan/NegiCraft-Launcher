using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NegiCraftLauncher.App.Avalonia.Services;

namespace NegiCraftLauncher.App.Avalonia.Views;

/// <summary>
/// 背景调节窗口（Avalonia 侧）：与 WPF 的 <c>App/Views/BackgroundTuningWindow</c> 同一件事。
///
/// <para>它<b>只读不写业务</b> —— 滑杆全部直接绑在共享的 <c>MainWindowViewModel</c> 上，
/// 文件选择框和 Wallpaper Engine 同步仍然归主窗口，这里只把请求转出去。</para>
///
/// <para>与 WPF 侧唯一的实质差别是拖动：那边 <c>DragMove()</c> 阻塞到松手，所以能在它返回后
/// 上报位置；这边 <c>BeginMoveDrag()</c> 只是把拖动交给系统、立刻返回，所以改听
/// <see cref="TopLevel.PositionChanged"/>，并用 <c>_placing</c> 把"我们自己摆的位置"过滤掉。</para>
/// </summary>
public sealed partial class BackgroundTuningWindow : Window
{
    /// <summary>请求弹"选择背景图片"。文件框归主窗口弹（owner 得是主窗口）。</summary>
    public event Action? PickBackgroundRequested;

    /// <summary>请求从 Wallpaper Engine 同步一张过来。</summary>
    public event Action? SyncWallpaperEngineRequested;

    /// <summary>用户拖完之后上报新的物理位置，让主窗口存下来。</summary>
    public event Action<PixelPoint>? Moved;

    /// <summary>主窗口自动摆位时置位：那不是用户拖的，不该被存成"用户摆过的位置"。</summary>
    internal bool Placing;

    public BackgroundTuningWindow()
    {
        InitializeComponent();

        // 不进任务栏也不进 Alt+Tab：它是主窗口的附属小窗，跟着主窗口收放。
        ToolWindowStyle.ApplyToolWindow(this);

        PositionChanged += (_, _) =>
        {
            if (Placing) return;
            Moved?.Invoke(Position);
        };
    }

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // BeginMoveDrag 立刻返回（真正的移动由系统驱动），所以位置靠 PositionChanged 上报。
        BeginMoveDrag(e);
    }

    private void OnPickImageClick(object? sender, RoutedEventArgs e) => PickBackgroundRequested?.Invoke();

    private void OnSyncWallpaperEngineClick(object? sender, RoutedEventArgs e) => SyncWallpaperEngineRequested?.Invoke();
}
