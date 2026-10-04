using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace NegiCraftLauncher.App.Probe;

/// <summary>
/// 探针 4 专用：一个把子 HWND 涂成纯绿的 <see cref="HwndHost"/>。
///
/// <para>如果它在一扇 <c>AllowsTransparency=True</c> 的分层窗里还能看见绿色，说明
/// 计划 §5.1「WPF 分层窗无法承载子 HWND」的结论是错的，GL 路线还有救；
/// 如果看不见，就确认了必须换软件光栅化后端。</para>
/// </summary>
internal sealed class ProbeHwndHost : HwndHost
{
    private const string ClassName = "NclProbeHostWindow";

    private static bool _classRegistered;

    /// <summary>必须作为字段常驻，否则委托会被 GC 回收，窗口过程变成野指针。</summary>
    private static readonly Win32.WndProc WndProcDelegate = Win32.DefWindowProc;

    private IntPtr _hwnd;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var instance = Win32.GetModuleHandle(null);

        if (!_classRegistered)
        {
            var wc = new Win32.WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
                hInstance = instance,
                // COLORREF 是 0x00BBGGRR，所以 0x0000FF00 就是纯绿。
                hbrBackground = Win32.CreateSolidBrush(0x0000FF00),
                lpszClassName = ClassName,
            };
            Win32.RegisterClass(ref wc);
            _classRegistered = true;
        }

        _hwnd = Win32.CreateWindowEx(
            0, ClassName, "", Win32.WS_CHILD | Win32.WS_VISIBLE,
            0, 0, 64, 64, hwndParent.Handle, IntPtr.Zero, instance, IntPtr.Zero);

        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero) Win32.DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }
}
