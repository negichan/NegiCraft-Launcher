using System.Collections.Generic;
using System.ComponentModel;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// Everything the pet window needs from whoever is hosting it.
///
/// The launcher embeds the pet in-process and supplies an adapter over its view model, so the pet
/// can inherit the current account name, offer accounts in the skin menu, and reopen the launcher.
/// The standalone build supplies a minimal host instead: no accounts, no launcher.
///
/// A null host means "no host at all" — the pet then just keeps the name it was constructed with.
/// </summary>
public interface IPetHost
{
    /// <summary>User-chosen pet name; null or blank means "inherit <see cref="EffectiveName"/>".</summary>
    string? CustomName { get; set; }

    /// <summary>Name the pet inherits while <see cref="CustomName"/> is unset (e.g. current account).</summary>
    string EffectiveName { get; }

    /// <summary>Account names offered by the "更换皮肤" menu. Empty means no account entries.</summary>
    IReadOnlyList<string> AccountNames { get; }

    /// <summary>Whether the pet may offer "打开启动器". False in the standalone build.</summary>
    bool CanOpenLauncher { get; }

    /// <summary>Brings the launcher to the front.</summary>
    void OpenLauncher();

    /// <summary>
    /// Raised with <see cref="EffectiveName"/> when the inherited name changes (e.g. the user
    /// switches accounts in the launcher). Kept separate from <see cref="CustomName"/> changes,
    /// which the pet itself originates and therefore already knows about.
    /// </summary>
    event PropertyChangedEventHandler? PropertyChanged;
}
