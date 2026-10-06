using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace NegiCraftLauncher.Pet.Services;

/// <summary>
/// 监听全局鼠标输入以在点击菜单外部时自动关闭 ContextMenu。
///
/// <para>解决 <c>WS_EX_NOACTIVATE</c> 工具窗口因从未获取/失去系统焦点，
/// 导致 WPF 默认的 ContextMenu / Popup 无法在点击桌面、外部应用或桌宠窗口时自动关闭的问题。</para>
/// </summary>
public sealed class MenuDismissTracker : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ContextMenu[] _menus;
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook = IntPtr.Zero;

    public bool HasOpenMenu
    {
        get
        {
            foreach (var m in _menus)
            {
                if (m.IsOpen) return true;
            }
            return false;
        }
    }

    public MenuDismissTracker(Dispatcher dispatcher, params ContextMenu[] menus)
    {
        _dispatcher = dispatcher;
        _menus = menus;
        _proc = HookCallback;

        foreach (var m in _menus)
        {
            m.Opened += OnMenuOpened;
            m.Closed += OnMenuClosed;
        }
    }

    private void OnMenuOpened(object? sender, RoutedEventArgs e)
    {
        InstallHook();
    }

    private void OnMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (!HasOpenMenu)
        {
            UninstallHook();
        }
    }

    public void CloseAll()
    {
        foreach (var m in _menus)
        {
            if (m.IsOpen) m.IsOpen = false;
        }
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
    }

    private void UninstallHook()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int message = wParam.ToInt32();
            if (message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or
                       WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN or WM_NCMBUTTONDOWN or
                       WM_MOUSEWHEEL)
            {
                try
                {
                    var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (IsClickOutside(ms.pt))
                    {
                        _dispatcher.BeginInvoke(CloseAll);
                    }
                }
                catch
                {
                    // 钩子回调决不能抛出异常，否则会被 Windows 静默移除
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool IsClickOutside(POINT pt)
    {
        if (!HasOpenMenu) return false;

        IntPtr clickedHwnd = WindowFromPoint(pt);
        if (clickedHwnd == IntPtr.Zero) return true;

        GetWindowThreadProcessId(clickedHwnd, out uint pid);
        if (pid != (uint)Environment.ProcessId)
        {
            // 点击发生在其他进程（桌面、任务栏、其他应用程序等）
            return true;
        }

        // 本进程内窗口：若是普通 Window（如桌宠窗口、启动器主窗口、对话框），则属于菜单外部
        var source = HwndSource.FromHwnd(clickedHwnd);
        if (source == null || source.RootVisual is Window)
        {
            return true;
        }

        // 否则 RootVisual 是 PopupRoot，即正在点击右键菜单或子菜单本身
        return false;
    }

    /// <summary>
    /// 测试给定屏幕物理坐标处的点击是否判定为外部点击。若为外部点击，则关闭菜单。
    /// </summary>
    public bool TestClick(int screenX, int screenY)
    {
        var pt = new POINT { x = screenX, y = screenY };
        bool outside = IsClickOutside(pt);
        if (outside)
        {
            CloseAll();
        }
        return outside;
    }

    public void Dispose()
    {
        foreach (var m in _menus)
        {
            m.Opened -= OnMenuOpened;
            m.Closed -= OnMenuClosed;
        }
        UninstallHook();
    }

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCRBUTTONDOWN = 0x00A4;
    private const int WM_NCMBUTTONDOWN = 0x00A7;

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
