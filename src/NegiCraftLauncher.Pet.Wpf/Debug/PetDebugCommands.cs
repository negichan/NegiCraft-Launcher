using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NegiCraftLauncher.Skin.Wpf.Controls;

namespace NegiCraftLauncher.Pet.Wpf.Debug;

/// <summary>
/// 桌宠动词，启动器的调试桥与独立桌宠的桥共用。
///
/// <para><b>本文件是 <c>Pet/Debug/PetDebugCommands.cs</c> 的 WPF 移植版。</b>
/// 动词名、参数解析、回复字符串**逐字节一致** —— <c>design/_dbg.ps1</c> 与
/// <c>AGENTS.md</c> 里的用法两边通用。改动词或回复格式必须同时改 Avalonia 那份。</para>
///
/// <para>这里的每个处理分支只需要一个 <see cref="PetWindow"/>（名字/状态类动词再要一个
/// <see cref="IPetHost"/>）。需要启动器状态的东西（页面、账户、下载、托盘）留在启动器自己的桥里，
/// 独立版够不着。</para>
///
/// <para>平台替换：<c>GetVisualDescendants().OfType&lt;MinecraftSkinPreview&gt;()</c>
/// → <see cref="PetWindow.Preview"/>（WPF 桌宠直接暴露控件字段，比遍历可视树稳）；
/// <c>TryGetPlatformHandle().Handle</c> → <see cref="WindowInteropHelper"/>；
/// <c>RenderTargetBitmap</c> + <c>PngBitmapEncoderOptions</c> → WPF 的
/// <see cref="PngBitmapEncoder"/>。</para>
/// </summary>
public static class PetDebugCommands
{
    /// <summary>
    /// 处理只需要桌宠窗口的动词。动词不属于桌宠时返回 <c>null</c>，让调用方落到自己的
    /// （启动器专属）动词上。
    ///
    /// <para><paramref name="pet"/> 可以是 null：启动器允许桌宠窗口关着。那种情况下桌宠动词
    /// 回 <c>ERR no pet window</c>，与一直以来的行为一致。</para>
    ///
    /// <para><paramref name="referenceHwnd"/> 是宿主顶层窗口的句柄（如果有）。只有
    /// <c>pet-hwnd</c> 用它来判断桌宠的 owner 是不是宿主窗口；独立桌宠没有宿主窗口，
    /// 传 <see cref="IntPtr.Zero"/>，该字段就不出现在回复里。</para>
    /// </summary>
    public static Task<string>? TryHandle(
        PetWindow? pet, bool isActive, string verb, string arg, IntPtr referenceHwnd = default)
    {
        if (pet is null)
        {
            return IsPetVerb(verb) ? Task.FromResult("ERR no pet window") : null;
        }

        switch (verb)
        {
            case "pet-dangle":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    preview.IsDangling = Truthy(arg);
                    return "OK " + preview.IsDangling;
                });
            case "pet-rotate":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    if (float.TryParse(arg, out var deg))
                    {
                        preview.RotateModel(deg);
                        return "OK " + deg;
                    }
                    return "ERR invalid deg";
                });
            case "pet-sneak":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    preview.Sneaking = Truthy(arg);
                    return "OK " + preview.Sneaking;
                });
            case "pet-control":
                return PetDebugMailbox.Ui(() =>
                {
                    if (arg == "on" || arg == "true" || arg == "1") pet.IsControlMode = true;
                    else if (arg == "off" || arg == "false" || arg == "0") pet.IsControlMode = false;
                    else if (arg == "toggle") pet.IsControlMode = !pet.IsControlMode;
                    return "OK " + pet.IsControlMode;
                });
            case "pet-key":
                return PetDebugMailbox.Ui(() =>
                {
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) return "ERR missing key";
                    var keyName = parts[0];
                    bool down = parts.Length < 2 || parts[1] == "down" || parts[1] == "1" || parts[1] == "true";
                    pet.SetSimulatedKey(keyName, down);
                    return $"OK key={keyName} down={down}";
                });
            case "pet-follow":
                return PetDebugMailbox.Ui(() =>
                {
                    if (arg == "on" || arg == "true" || arg == "1") pet.IsFollowMouseMode = true;
                    else if (arg == "off" || arg == "false" || arg == "0") pet.IsFollowMouseMode = false;
                    else if (arg == "toggle") pet.IsFollowMouseMode = !pet.IsFollowMouseMode;
                    return "OK " + pet.IsFollowMouseMode;
                });
            case "pet-interact":
                return PetDebugMailbox.Ui(() =>
                {
                    // pet-interact [on|off|toggle] | pet-interact swallow [on|off|toggle]
                    var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0] == "swallow")
                    {
                        if (parts[1] == "on") pet.InteractSwallowClicks = true;
                        else if (parts[1] == "off") pet.InteractSwallowClicks = false;
                        else if (parts[1] == "toggle") pet.InteractSwallowClicks = !pet.InteractSwallowClicks;
                    }
                    else if (parts.Length >= 1)
                    {
                        if (parts[0] == "on") pet.IsInteractMode = true;
                        else if (parts[0] == "off") pet.IsInteractMode = false;
                        else if (parts[0] == "toggle") pet.IsInteractMode = !pet.IsInteractMode;
                    }
                    return $"OK interact={pet.IsInteractMode} swallow={pet.InteractSwallowClicks} hook={pet.IsInteractHookInstalled}";
                });
            case "pet-attack":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    // 带进度参数就把挥拳停在那一刻，方便拍照；不带就正常播一遍。
                    if (double.TryParse(arg, out double t))
                    {
                        preview.TriggerAttack(Math.Clamp(t, 0.0, 1.0));
                        return "OK parked " + t;
                    }
                    preview.TriggerAttack();
                    return "OK played";
                });
            case "pet-coord":
                return PetDebugMailbox.Ui(() =>
                {
                    var parts = arg.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy))
                    {
                        pet.SetNavigationTarget(cx, cy);
                        for (int i = 2; i + 1 < parts.Length; i += 2)
                        {
                            if (int.TryParse(parts[i], out int nx) && int.TryParse(parts[i + 1], out int ny))
                            {
                                pet.AddNavigationTarget(nx, ny);
                            }
                        }
                        return $"OK waypoints={pet.RemainingWaypointCount}";
                    }
                    return "ERR invalid coords";
                });
            case "pet-coord-add":
                return PetDebugMailbox.Ui(() =>
                {
                    var parts = arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy))
                    {
                        pet.AddNavigationTarget(cx, cy);
                        return $"OK waypoints={pet.RemainingWaypointCount}";
                    }
                    return "ERR invalid coords";
                });
            case "pet-mode":
                return PetDebugMailbox.Ui(() =>
                {
                    // PetInteractionMode 现在在 NegiCraftLauncher.Pet.Core（共享层），
                    // 不再是 PetWindow 的嵌套类型。
                    if (arg == "free") pet.CurrentMode = PetInteractionMode.Free;
                    else if (arg == "control") pet.CurrentMode = PetInteractionMode.Control;
                    else if (arg == "follow") pet.CurrentMode = PetInteractionMode.FollowMouse;
                    return "OK mode=" + pet.CurrentMode;
                });
            case "pet-menu":
                return PetDebugMailbox.Ui(() =>
                {
                    if (arg == "close") pet.ClosePetContextMenu();
                    else pet.OpenPetContextMenu();
                    return "OK IsOpen=" + pet.IsPetContextMenuOpen;
                });
            case "pet-dialog":
                // 两个"只能靠点菜单才出得来"的窗口：改名对话框与全屏选点遮罩。
                // 不加这个动词就只能靠人手动点，回归时覆盖不到。
                // 用法：pet-dialog <name|coord|close> [png 绝对路径]
                // 给了路径就把那个窗口的内容离屏渲染成 PNG（窗口本身照旧留在屏幕上）。
                return PetDebugMailbox.Ui(() =>
                {
                    var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    var which = parts.Length > 0 ? parts[0] : "";
                    var shot = parts.Length > 1 ? parts[1].Trim() : null;

                    switch (which)
                    {
                        case "name":
                            pet.OpenNameDialogForDebug(shot);
                            return "OK dialog=name";
                        case "coord":
                            pet.OpenCoordPickForDebug(shot);
                            return "OK dialog=coord";
                        case "close":
                            pet.CloseDebugDialogs();
                            return "OK dialog=closed";
                        default:
                            return "ERR unknown dialog " + which;
                    }
                });
            case "pet-walk":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    preview.IsWalking = Truthy(arg);
                    return "OK " + preview.IsWalking;
                });
            case "pet-jump":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    preview.IsJumping = Truthy(arg);
                    return "OK " + preview.IsJumping;
                });
            case "pet-jump-offset":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    if (double.TryParse(arg, out double off))
                    {
                        preview.SetJumpOffset(off);
                        return "OK " + off;
                    }
                    return "ERR invalid offset";
                });
            case "pet-yaw":
                return PetDebugMailbox.Ui(() =>
                {
                    if (Preview(pet) is not { } preview) return "ERR no pet preview";
                    if (float.TryParse(arg, out float deg))
                    {
                        preview.RotateModel(deg - preview.CurrentYawDeg);
                    }
                    return "OK " + preview.CurrentYawDeg;
                });
            case "pet-track":
                return PetDebugMailbox.Ui(() => "OK " + pet.TrackDebugInfo);
            case "pet-mouse":
                return PetDebugMailbox.Ui(() =>
                {
                    if (arg == "reset" || arg == "clear")
                    {
                        pet.ClearVirtualCursor();
                        return "OK clear";
                    }
                    var parts = arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int mx) && int.TryParse(parts[1], out int my))
                    {
                        pet.SetVirtualCursor(mx, my);
                        return $"OK mouse=({mx},{my})";
                    }
                    return "ERR invalid mouse coords";
                });
            case "pet-name":
                return PetDebugMailbox.Ui(() =>
                {
                    var host = pet.Host;
                    if (arg == "reset" || string.IsNullOrWhiteSpace(arg))
                    {
                        if (host != null)
                        {
                            host.CustomName = null;
                            pet.SetPlayerName(host.EffectiveName);
                        }
                        return "OK " + (host?.EffectiveName ?? "");
                    }

                    if (host != null) host.CustomName = arg;
                    pet.SetPlayerName(arg);
                    return "OK " + (host?.EffectiveName ?? arg);
                });
            case "pet-skinsnap":
                return PetSnapshot(pet, arg);
            case "shot-pet":
                return PetDebugMailbox.Ui(() => PetShot(pet, arg));
            case "pet-state":
                // 桌宠实时状态只读视图：它真正在渲染的名字（由宿主适配器负责保持同步），
                // 外加模式 / 置顶 / 菜单开合。
                return PetDebugMailbox.Ui(() =>
                {
                    var preview = Preview(pet);
                    var host = pet.Host;
                    var accounts = host is null ? "" : string.Join(",", host.AccountNames);
                    return $"petActive={isActive} petPlayerName={preview?.PlayerName ?? "n/a"} " +
                           $"effectiveName={host?.EffectiveName ?? "<null>"} customName={host?.CustomName ?? "<null>"} " +
                           $"mode={pet.CurrentMode} topmost={pet.Topmost} " +
                           $"menuOpen={pet.IsPetContextMenuOpen} accounts=[{accounts}]";
                });
            case "pet-hwnd":
                // 只读探测桌宠窗口真实的 Win32 样式。这决定了 Alt+Tab / 任务视图 / 抢前台
                // 能不能靠注入 WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE 修好。
                return PetDebugMailbox.Ui(() => PetHwnd(pet, referenceHwnd));
            default:
                return null;
        }
    }

    private static bool IsPetVerb(string verb) =>
        verb.StartsWith("pet-", StringComparison.Ordinal) || verb == "shot-pet";

    private static bool Truthy(string arg) => arg == "on" || arg == "true" || arg == "1";

    /// <summary>
    /// WPF 桌宠把预览控件暴露成属性（<see cref="PetWindow.Preview"/>），
    /// 不用像 Avalonia 那样遍历可视树去找。
    /// </summary>
    private static SkinPreviewControl? Preview(PetWindow pet) => pet.Preview;

    /// <summary>离屏渲染桌宠窗口的内容；不需要窗口可见、也不需要焦点。</summary>
    private static string PetShot(PetWindow pet, string path)
    {
        if (pet.Content is not FrameworkElement content) return "ERR no pet window";

        var size = content.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(pet.Width, pet.Height);

        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height),
            96, 96, PixelFormats.Pbgra32);
        rtb.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return $"OK {path}";
    }

    /// <summary>
    /// 抓桌宠的实时皮肤帧。
    ///
    /// <para><b>与 Avalonia 版的差别</b>：那边 GL 的 <c>SaveSnapshot</c> 交回的是**上一次**请求
    /// 捕获的帧，所以必须"先上膛、等 90ms、再开火"；WPF 这边是软件光栅化、同步且确定性，
    /// 一次调用就是当前帧。回复格式保持一致，客户端不用改。</para>
    /// </summary>
    private static Task<string> PetSnapshot(PetWindow pet, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Task.FromResult("ERR no path");

        return PetDebugMailbox.Ui(() =>
        {
            var preview = Preview(pet);
            if (preview == null) return "ERR no pet preview";
            preview.SaveSnapshot(path);
            return "OK " + path;
        });
    }

    // --- Win32 样式探测 (pet-hwnd) -------------------------------------------------------------
    // style / ex-style 是 32 位 DWORD，所以这里该用 GetWindowLongW（不是 ...Ptr），
    // x86 / x64 都对，不需要平台分支。
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const uint GW_OWNER = 4;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CAPTION = 0x00C00000;

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private static string PetHwnd(PetWindow pet, IntPtr referenceHwnd)
    {
        var hwnd = new WindowInteropHelper(pet).Handle;
        if (hwnd == IntPtr.Zero) return "ERR no hwnd";

        int style = GetWindowLongW(hwnd, GWL_STYLE);
        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        var owner = GetWindow(hwnd, GW_OWNER);

        var host = referenceHwnd == IntPtr.Zero
            ? ""
            : $" mainHwnd=0x{referenceHwnd.ToInt64():X} ownerIsMain={owner == referenceHwnd}";

        return $"hwnd=0x{hwnd.ToInt64():X} owner=0x{owner.ToInt64():X} " +
               $"style=0x{style:X8} exstyle=0x{exStyle:X8} | " +
               $"toolwindow={Has(exStyle, WS_EX_TOOLWINDOW)} " +
               $"noactivate={Has(exStyle, WS_EX_NOACTIVATE)} " +
               $"appwindow={Has(exStyle, WS_EX_APPWINDOW)} " +
               $"layered={Has(exStyle, WS_EX_LAYERED)} " +
               $"transparent={Has(exStyle, WS_EX_TRANSPARENT)} " +
               $"topmost={Has(exStyle, WS_EX_TOPMOST)} | " +
               $"popup={Has(style, WS_POPUP)} caption={Has(style, WS_CAPTION)} " +
               $"visible={Has(style, WS_VISIBLE)} | " +
               $"petClass={ClassName(hwnd)} ownerClass={ClassName(owner)} " +
               $"ownerTitle='{WindowText(owner)}' ownerVisible={IsWindowVisible(owner)}" + host;
    }

    private static bool Has(int value, int flag) => (value & flag) != 0;

    private static string ClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<null>";
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "<err>";
    }

    private static string WindowText(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "<null>";
        var sb = new StringBuilder(256);
        return GetWindowTextW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
