using Avalonia.Controls;

namespace NegiCraftLauncher.App.Avalonia.Services;

/// <summary>
/// 把窗口标成 tool window：不进 Alt+Tab / 任务视图。与 WPF 侧
/// <c>App/Services/ToolWindowStyle.cs</c> 同一件事，做法不同 ——
/// Avalonia 没有 ToolWindow 概念，<c>ShowInTaskbar=false</c> 只去掉任务栏按钮。
///
/// <para>唯一的钩子是 <see cref="Win32Properties.AddWindowStylesCallback"/>：Win32 后端每次
/// 更新窗口样式都会重跑它（Avalonia 自己也会改 style，所以不能像 WPF 那样改一次就完事）。</para>
///
/// <para><b>刻意不加 <c>WS_EX_NOACTIVATE</c></b>：桌宠那扇窗不能抢焦点是因为它常驻桌面，
/// 而这扇窗是用户主动点开、要伸手拖滑杆的，点了不聚焦反而没法用键盘微调。
/// 非 Win32 后端（Linux/macOS）会直接忽略这个回调，那时它退化成普通子窗，无害。</para>
/// </summary>
public static class ToolWindowStyle
{
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    // 单例回调：Add / Remove 必须是同一个委托实例才配得对。
    private static readonly Win32Properties.CustomWindowStylesCallback Callback =
        static (style, exStyle) => (style, exStyle | WS_EX_TOOLWINDOW);

    /// <summary>要在窗口还活着的时候调用（<c>Show()</c> 之前最好）；样式在下次更新窗口属性时落地。</summary>
    public static void ApplyToolWindow(TopLevel topLevel) =>
        Win32Properties.AddWindowStylesCallback(topLevel, Callback);
}
