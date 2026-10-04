using System.Runtime.InteropServices;

namespace NegiCraftLauncher.Pet.Services;

/// <summary>
/// 物理键盘的"按住没按住"查询。桌宠的 WASD / 空格 / Shift / Ctrl 都走这里，
/// 而不是等窗口的 <c>KeyDown</c> —— 桌宠窗口带 <c>WS_EX_NOACTIVATE</c>，基本拿不到焦点。
///
/// <para>纯 P/Invoke，无框架依赖。非 Windows 上恒返回 false（桌宠只在 Windows 上跑物理）。</para>
/// </summary>
public static class PetNativeKeys
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkSpace = 0x20;
    public const int VkA = 0x41;
    public const int VkD = 0x44;
    public const int VkS = 0x53;
    public const int VkW = 0x57;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>这个虚拟键现在按着吗（最高位 = 当前按下）。</summary>
    public static bool IsDown(int virtualKey)
    {
        if (!OperatingSystem.IsWindows()) return false;
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }
}
