using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Pet.Debug;

namespace NegiCraftLauncher.Pet.App;

/// <summary>
/// The standalone pet's debug bridge.
///
/// Same file-mailbox protocol as the launcher's, but with only the pet verbs plus <c>quit</c> —
/// there are no pages, accounts, downloads or tray to drive here. It lives in its own mailbox
/// (<c>%TEMP%\ncl-pet-debug</c>) so the launcher and the standalone pet can be debugged at the same
/// time without stealing each other's commands.
/// </summary>
internal static class StandaloneDebugBridge
{
    /// <summary>Mailbox directory under <c>%TEMP%</c>. Override with <c>--debug-box &lt;name&gt;</c>.</summary>
    public const string DefaultBoxName = "ncl-pet-debug";

    // Exactly one bridge per process, so the dispatch closure can reach the mailbox it answers on.
    private static PetDebugMailbox? _mailbox;

    /// <summary>Starts the bridge when the process was launched with <c>--debug</c>; otherwise does nothing.</summary>
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
            // The pet is the only window here, so it is "active" whenever it exists; it has no host
            // top-level window to compare owners against, hence the zero handle.
            if (PetDebugCommands.TryHandle(pet, isActive: true, verb, arg) is { } shared)
            {
                return await shared;
            }

            switch (verb)
            {
                // Closes the pet window exactly as the "关闭桌宠" menu item does. Kept separate from
                // `quit` on purpose: `quit` tears the process down, `pet-close` only closes the window,
                // so the two exit paths can be told apart when testing shutdown behaviour.
                case "pet-close":
                    await PetDebugMailbox.Ui(pet.Close);
                    return "OK";
                case "quit":
                    // Exit only once the reply is on disk, so the client sees OK rather than timing out.
                    _mailbox?.ExitAfterReply(() =>
                        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
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
