using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// 把窗口标成 tool window：不进任务栏、也不进 Alt+Tab。
///
/// <para><c>ShowInTaskbar=false</c> 只去掉任务栏按钮，窗口仍然出现在 Alt+Tab / 任务视图里。
/// 背景调节窗是启动器的附属小窗，跟着主窗口收放，单独占一个 Alt+Tab 位置没必要。
/// 那个位在 WPF 里没有对应属性，只能拿 HWND 自己改 —— 和桌宠窗口同一套做法
/// （见 <c>Pet/Services/PetShellStyle.cs</c>）。</para>
///
/// <para><b>刻意不加 <c>WS_EX_NOACTIVATE</c></c>：桌宠不能抢焦点是因为它常驻桌面，
/// 而这扇窗是用户主动点开、要伸手进去拖滑杆的，点了不聚焦反而没法用键盘微调。</para>
/// </summary>
public static class ToolWindowStyle
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;

    // ex-style 是 32 位 DWORD，所以用 Get/SetWindowLongW（不是 ...Ptr），x86 / x64 都对。
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>窗口还没 <c>Show()</c> 时 HWND 不存在，此时挂到 <c>SourceInitialized</c> 上。</summary>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyOnce(window);
            return;
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.SourceInitialized -= OnSourceInitialized;
        ApplyOnce(window);
    }

    private static void ApplyOnce(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var exStyle = GetWindowLongW(handle, GWL_EXSTYLE);
        exStyle |= WS_EX_TOOLWINDOW;
        exStyle &= ~WS_EX_APPWINDOW;
        SetWindowLongW(handle, GWL_EXSTYLE, exStyle);
    }
}
