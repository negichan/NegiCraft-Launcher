using System;
using System.Runtime.InteropServices;

namespace NegiCraftLauncher.Pet.Services;

/// <summary>
/// A low-level keyboard hook (WH_KEYBOARD_LL) to capture global hotkeys such as Left Alt + Caps Lock
/// to toggle intercept mode without changing system Caps Lock state or requiring focus.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    public event EventHandler? ToggleInterceptModePressed;

    public bool IsInstalled => _hook != IntPtr.Zero;

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_CAPITAL = 0x14;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private bool _capsDown;
    private bool _swallowCapsUp;

    public GlobalKeyboardHook() => _proc = HookCallback;

    public bool Install()
    {
        if (_hook != IntPtr.Zero) return true;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int message = wParam.ToInt32();
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                if (kbd.vkCode == VK_CAPITAL)
                {
                    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                    {
                        if (!_capsDown)
                        {
                            _capsDown = true;
                            // Check if Left Alt is held down (specifically Left Alt, not Right Alt)
                            bool leftAltDown = (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0 ||
                                              ((GetAsyncKeyState(VK_MENU) & 0x8000) != 0 && (GetAsyncKeyState(VK_RMENU) & 0x8000) == 0);

                            if (leftAltDown)
                            {
                                _swallowCapsUp = true;
                                ToggleInterceptModePressed?.Invoke(this, EventArgs.Empty);
                                return (IntPtr)1; // Swallow CapsLock down so system Caps Lock state/LED does not flip
                            }
                        }
                        else if (_swallowCapsUp)
                        {
                            // Swallow auto-repeat while holding CapsLock with Alt
                            return (IntPtr)1;
                        }
                    }
                    else if (message == WM_KEYUP || message == WM_SYSKEYUP)
                    {
                        _capsDown = false;
                        if (_swallowCapsUp)
                        {
                            _swallowCapsUp = false;
                            return (IntPtr)1; // Swallow CapsLock up
                        }
                    }
                }
            }
            catch
            {
                // Never allow exceptions to escape the native hook callback
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
