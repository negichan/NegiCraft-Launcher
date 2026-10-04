using System.Windows;
using NegiCraftLauncher.Pet.Wpf;
using NegiCraftLauncher.Pet.Wpf.Debug;

namespace NegiCraftLauncher.Pet.App.Wpf;

/// <summary>
/// 独立桌宠的调试桥。
///
/// <para>与启动器那套同一份文件邮箱协议，但只有桌宠动词加一个 <c>quit</c> ——
/// 这里没有页面、账号、下载、托盘可驱动。它住在自己的邮箱
/// （<c>%TEMP%\ncl-pet-debug</c>）里，所以启动器和独立桌宠可以同时调试、互不抢命令。</para>
///
/// <para>与 Avalonia 版 <c>Pet.App/StandaloneDebugBridge.cs</c> 行为一致。</para>
/// </summary>
internal static class StandaloneDebugBridge
{
    /// <summary><c>%TEMP%</c> 下的邮箱目录名。可用 <c>--debug-box &lt;name&gt;</c> 改。</summary>
    public const string DefaultBoxName = "ncl-pet-debug";

    // 每个进程恰好一个桥，所以分派闭包能拿到自己应答的那个邮箱。
    private static PetDebugMailbox? _mailbox;

    /// <summary>进程带 <c>--debug</c> 启动时才开桥；否则什么都不做。</summary>
    public static void StartIfNeeded(PetWindow pet)
    {
        if (!PetDebugMailbox.IsEnabled) return;

        var box = PetDebugMailbox.ResolveBoxName(DefaultBoxName);
        _mailbox = new PetDebugMailbox(box, "negipet-debug", line => Dispatch(pet, line));
        _mailbox.Start();
    }

    private static async Task<string> Dispatch(PetWindow pet, string line)
    {
        var parts = line.Split(' ', 2);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1] : "";

        try
        {
            // 桌宠是这里唯一的窗口，所以只要它在就算"激活"；它也没有宿主顶层窗口可以比对
            // owner，因此传零句柄。
            if (PetDebugCommands.TryHandle(pet, isActive: true, verb, arg) is { } shared)
            {
                return await shared;
            }

            switch (verb)
            {
                // 完全按「关闭桌宠」菜单项的方式关窗。与 `quit` 分开是有意的：`quit` 直接拆进程，
                // `pet-close` 走窗口，这样测退出行为时两条路可以分辨。
                // 配合 ShutdownMode.OnLastWindowClose，这条也会结束进程 —— 所以要用
                // ExitAfterReply，否则回复还没落盘进程就没了，客户端只能等到超时。
                case "pet-close":
                    _mailbox?.ExitAfterReply(pet.Close);
                    return "OK";
                case "quit":
                    // 回复落盘之后再退出，客户端才能看到 OK 而不是超时。
                    _mailbox?.ExitAfterReply(() => Application.Current?.Shutdown());
                    return "OK";
                default:
                    return $"ERR unknown verb {verb}";
            }
        }
        catch (Exception ex)
        {
            return $"ERR {ex.Message}";
        }
    }
}
