using System;
using System.Runtime.InteropServices;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// A WH_MOUSE_LL hook installed on the calling thread's message pump, so a left click anywhere
/// on the desktop can be read as a Minecraft left click. The callback sits inside the system's
/// input path: it must stay cheap and must never let an exception escape, because Windows
/// silently removes a hook that throws or times out.
/// </summary>
public sealed class GlobalMouseHook : IDisposable
{
    public sealed class LeftClickEventArgs : EventArgs
    {
        public int ScreenX { get; internal set; }
        public int ScreenY { get; internal set; }
        public bool AltDown { get; internal set; }
        public bool RightAltDown { get; internal set; }

        /// <summary>
        /// Set by the handler to keep this click, and the button-up that follows it, from
        /// reaching whatever is underneath.
        /// </summary>
        public bool Swallow { get; set; }
    }

    public event EventHandler<LeftClickEventArgs>? LeftButtonDown;

    public bool IsInstalled => _hook != IntPtr.Zero;

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int VK_MENU = 0x12;
    private const int VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // MSLLHOOKSTRUCT.flags only carries injection bits, so the modifier state has to come from
    // the async key state instead of from the hook record.
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // Kept in a field: if the delegate were collected the native hook would call into freed memory.
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _downWasSwallowed;

    public GlobalMouseHook() => _proc = HookCallback;

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;

        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int message = wParam.ToInt32();
                if (message == WM_LBUTTONDOWN)
                {
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var args = new LeftClickEventArgs
                    {
                        ScreenX = ms.pt.x,
                        ScreenY = ms.pt.y,
                        AltDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0,
                        RightAltDown = (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0
                    };
                    LeftButtonDown?.Invoke(this, args);

                    // The button-up goes too, so the window below never sees half a click.
                    _downWasSwallowed = args.Swallow;
                    if (args.Swallow) return (IntPtr)1;
                }
                else if (message == WM_LBUTTONUP && _downWasSwallowed)
                {
                    _downWasSwallowed = false;
                    return (IntPtr)1;
                }
            }
            catch
            {
                // Losing one click beats losing the hook.
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _downWasSwallowed = false;
    }
}
