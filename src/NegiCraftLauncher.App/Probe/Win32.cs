using System;
using System.Runtime.InteropServices;

namespace NegiCraftLauncher.App.Probe;

/// <summary>P1 风险探针用到的 Win32 入口。正式代码里这些会被移到各自的功能模块。</summary>
internal static class Win32
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint WS_EX_TOPMOST = 0x00000008;

    public const int SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    // ---- GDI 屏幕取样 ----

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);

    [DllImport("gdi32.dll")]
    public static extern int GetPixel(IntPtr hdc, int x, int y);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateSolidBrush(uint color);

    // ---- 子窗口宿主（探针 4） ----

    public const uint WS_CHILD = 0x40000000;
    public const uint WS_VISIBLE = 0x10000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    /// <summary>截取屏幕一块区域，返回 <c>0x00RRGGBB</c> 的像素快照（行优先）。</summary>
    public static uint[] CaptureScreen(int x, int y, int width, int height)
    {
        var pixels = new uint[width * height];

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, bitmap);

        try
        {
            BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY);

            for (var py = 0; py < height; py++)
            {
                for (var px = 0; px < width; px++)
                {
                    // GetPixel 返回 COLORREF：0x00BBGGRR。统一成 0xRRGGBB 方便比对。
                    var c = (uint)GetPixel(memDc, px, py);
                    pixels[py * width + px] = ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
                }
            }
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return pixels;
    }

    public static uint At(uint[] buffer, int width, int x, int y) => buffer[y * width + x];
}
