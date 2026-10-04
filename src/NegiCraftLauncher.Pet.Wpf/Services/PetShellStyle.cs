using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NegiCraftLauncher.Pet.Wpf.Services;

/// <summary>
/// 让桌宠窗口表现得像"桌面挂件"而不是普通应用窗口。
///
/// <para>WPF 的 <c>ShowInTaskbar=false</c> 只去掉任务栏按钮，窗口仍然进 Alt+Tab / 任务视图，
/// 点它仍然抢前台。修这两件事要的 ex-style 位在 WPF 里也没有对应属性，
/// 只能拿 HWND 自己 <c>SetWindowLong</c>：</para>
///
/// <list type="bullet">
/// <item><b>WS_EX_TOOLWINDOW</b> (0x00000080) —— 从 Alt+Tab 和任务视图里去掉。</item>
/// <item><b>WS_EX_NOACTIVATE</b> (0x08000000) —— 点击不再把它变成前台窗口。</item>
/// </list>
///
/// <para><b>与 Avalonia 版的差别</b>：那边靠 <c>Win32Properties.AddWindowStylesCallback</c>
/// 注册一个回调，由后端在每次更新窗口属性时重跑（因为 Avalonia 自己也会改写 style）。
/// WPF 没有这个钩子，改成在 <c>SourceInitialized</c> 里直接 <c>SetWindowLong</c> 改一次 ——
/// WPF 之后不会再动 ex-style，改一次就够。</para>
///
/// <para>两个位都是**加**上去的，原有的 <c>WS_EX_LAYERED</c>（<c>AllowsTransparency=True</c>
/// 带来的）、<c>WS_EX_TOPMOST</c> 都原样保留，所以桌宠照旧浮在全屏游戏之上。</para>
/// </summary>
public static class PetShellStyle
{
    private const int GWL_EXSTYLE = -20;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_APPWINDOW = 0x00040000;

    // style / ex-style 是 32 位 DWORD，所以用 Get/SetWindowLongW（不是 ...Ptr），
    // x86 / x64 都对，不需要平台分支。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>
    /// 给 <paramref name="window"/> 加上 tool-window / no-activate。
    ///
    /// <para>窗口还没 <c>Show()</c> 时 HWND 不存在，此时挂到 <c>SourceInitialized</c> 上 ——
    /// 那是 WPF 里 HWND 刚建好、还没上屏的时机，正好改 style。</para>
    /// </summary>
    public static void ApplyToolWindow(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            Apply(window);
            return;
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        Apply(window);
    }

    private static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var exStyle = GetWindowLongW(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        // WPF 的 ShowInTaskbar=false 本来就会清掉它，这里再清一次免得被别处加回来。
        exStyle &= ~WS_EX_APPWINDOW;

        SetWindowLongW(handle, GWL_EXSTYLE, exStyle);
    }
}
