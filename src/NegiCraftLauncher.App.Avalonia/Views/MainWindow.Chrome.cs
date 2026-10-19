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
/// 无边框窗口自己那圈边框（与 WPF 侧 <c>App/MainWindow.Chrome.cs</c> 对应）：还原窗口、
/// 拖拽移动、右下角缩放、调试用的命中测试、最小化/关闭、点空白关弹层。
///
/// <para>WPF 那份还多两件事 —— 右上角按钮取色和竖排音量浮层 —— Avalonia 这边
/// 没有对应实现（取色要 <c>RenderTargetBitmap</c> 采样，音量条是视频背景那条线的附属品），
/// 所以文件更短，不是漏搬。</para>
/// </summary>
public partial class MainWindow
{
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

    /// <summary>
    ///     拖边缘改大小：八条把手按下之后交给 <see cref="Window.BeginResizeDrag" />，
    ///     尺寸循环由系统跑（最小尺寸走 MinWidth/MinHeight）。把手的 Tag 写的是
    ///     <c>WindowEdge</c> 的成员名（Avalonia 12 是 Left / TopLeft …，没有 "Edge" 前缀）。
    /// </summary>
    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        // Tag 挂在 TemplatedControl 上（Border 就是它的派生），裸 object 上没有这个属性。
        if (sender is not global::Avalonia.Controls.Primitives.TemplatedControl { Tag: string name }) return;
        if (!Enum.TryParse<WindowEdge>(name, out var edge)) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginResizeDrag(edge, e);
    }

    /// <summary>
    ///     Debug bridge <c>hit</c>: which of the probed rects contain a point (window DIPs, e.g.
    ///     <c>hit 220,90</c>); <c>hit bounds</c> just reports the rects. Scripts can't touch the
    ///     mouse, so this is how "where should the probe go" and "whose area is this" get checked.
    ///     It answers geometry only — who actually receives the click is the z-order's business
    ///     (which is why the drag band sits under the content layer).
    /// </summary>
    public string HitDebug(string arg)
    {
        var probes = new (string Name, Control? Element)[]
        {
            ("band", DragBand), ("pill", FolderPillBtn), ("loader", LoaderFilterBtn),
            ("search", SearchBox), ("folderPop", FolderPop), ("loaderPop", LoaderPop)
        };

        if (arg == "bounds")
        {
            return string.Join(" ", probes.Select(p => $"{p.Name}=[{RectOf(p.Element)}]"));
        }

        var bits = arg.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (bits.Length < 2 ||
            !double.TryParse(bits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(bits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return "ERR usage: hit <x>,<y> | hit bounds";
        }

        var point = new Point(x, y);
        var on = probes.Where(p => WindowRect(p.Element).Contains(point)).Select(p => p.Name);
        return $"hit by={(on.Any() ? string.Join(",", on) : "none")}";
    }

    /// <summary>Rect in window DIPs. <c>TranslatePoint</c> follows RenderTransform, so an open
    /// popover reports where it actually parked (the Pop.Pin translation included).</summary>
    private Rect WindowRect(Control? element)
    {
        if (element is null) return default;
        var at = element.TranslatePoint(new Point(), this) ?? default;
        return new Rect(at, element.Bounds.Size);
    }

    private string RectOf(Control? element)
    {
        if (element is null) return "n/a";
        var r = WindowRect(element);
        return $"{r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#}";
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        _vm?.RequestClose();
    }

    private void OnPopoverBackgroundPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.CloseAllPopoversCommand.Execute(null);
    }

}
