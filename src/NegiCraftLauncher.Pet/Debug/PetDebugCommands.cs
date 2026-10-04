using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NegiCraftLauncher.Skin.Controls;

namespace NegiCraftLauncher.Pet.Debug;

/// <summary>
/// The pet verbs, shared by the launcher's debug bridge and the standalone pet's.
///
/// Every handler here needs nothing but a <see cref="PetWindow"/> (and, for the name/state verbs,
/// whatever <see cref="IPetHost"/> it was built with). Anything that also needs launcher state —
/// pages, accounts, downloads, the tray — stays in the launcher's own bridge and is not reachable
/// from the standalone build.
/// </summary>
public static class PetDebugCommands
{
    /// <summary>
    /// Handles a verb that only needs the pet window. Returns <c>null</c> when the verb is not a pet
    /// verb, so the caller can fall through to its own (launcher-only) verbs.
    ///
    /// <paramref name="pet"/> may be null: the launcher can have the pet window closed. In that case
    /// pet verbs answer <c>ERR no pet window</c>, the same reply they always gave.
    ///
    /// <paramref name="referenceHwnd"/> is the hosting top-level window's handle, when there is one.
    /// Only <c>pet-hwnd</c> uses it, to report whether the pet's owner is the host window; the
    /// standalone pet has no host window, so it passes <see cref="IntPtr.Zero"/> and the field is
    /// left out of the reply.
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
                    // A progress argument parks the swing there for photographing; bare plays it.
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
            case "pet-area":
                // 只读探测内核**真正用来夹取**的工作区（DIP）。多屏下这里能直接看出桌宠被夹在
                // 哪块屏 —— WPF 侧过去只报主屏（SystemParameters.WorkArea），是条真 bug。
                // 与 WPF 侧逐字节一致。
                return PetDebugMailbox.Ui(() =>
                {
                    var area = pet.CurrentWorkArea;
                    var win = pet.CurrentWindow;
                    return $"OK work=({area.X:F0},{area.Y:F0},{area.Width:F0},{area.Height:F0}) " +
                           $"window=({win.X:F0},{win.Y:F0})";
                });
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
                // Read-only view of the live pet: the name it is actually rendering (which the
                // host adapter is responsible for keeping in sync), plus mode/topmost/menu.
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
                // Read-only probe of the pet window's real Win32 styles. This is what decides
                // whether Alt+Tab / Task view / foreground-stealing can be fixed by injecting
                // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE (route A) or needs another approach.
                return PetDebugMailbox.Ui(() => PetHwnd(pet, referenceHwnd));
            default:
                return null;
        }
    }

    private static bool IsPetVerb(string verb) =>
        verb.StartsWith("pet-", StringComparison.Ordinal) || verb == "shot-pet";

    private static bool Truthy(string arg) => arg == "on" || arg == "true" || arg == "1";

    private static MinecraftSkinPreview? Preview(PetWindow pet) =>
        pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();

    /// <summary>Renders the pet window's content offscreen; no window visibility or focus required.</summary>
    private static string PetShot(PetWindow pet, string path)
    {
        if (pet.Content is not Visual content) return "ERR no pet window";

        var size = content.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(pet.Width, pet.Height);
        using var rtb = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)),
            new Vector(96, 96));
        rtb.Render(content);
        rtb.Save(path, new PngBitmapEncoderOptions());
        return $"OK {path}";
    }

    // shot-pet cannot see the pet: its offscreen render only ever draws the startup snapshot, so
    // every frame comes out identical. This reads the live GL frame instead. SaveSnapshot hands
    // back the frame captured by the *previous* request, hence arm once, wait, then save.
    private static async Task<string> PetSnapshot(PetWindow pet, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "ERR no path";

        var preview = await PetDebugMailbox.Ui(() => Preview(pet));
        if (preview == null) return "ERR no pet preview";

        var scratch = Path.Combine(Path.GetTempPath(), "ncl-pet-snap-arm.png");
        await PetDebugMailbox.Ui(() => preview.SaveSnapshot(scratch));
        await Task.Delay(90);
        await PetDebugMailbox.Ui(() => preview.SaveSnapshot(path));
        try { File.Delete(scratch); } catch (IOException) { }

        return "OK " + path;
    }

    // --- Win32 style probe (pet-hwnd) ---------------------------------------------------------
    // Style/ex-style are 32-bit DWORDs, so GetWindowLongW (not ...Ptr) is the correct call here
    // and works on both x86 and x64 without a platform guard.
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
        var handle = pet.TryGetPlatformHandle();
        if (handle == null || handle.Handle == IntPtr.Zero) return "ERR no hwnd";

        var hwnd = handle.Handle;
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
