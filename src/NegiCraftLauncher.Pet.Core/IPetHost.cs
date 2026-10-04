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

    /// <summary>
    /// 桌宠是否用 GPU 硬件渲染。
    ///
    /// <para>宿主只负责**存**：进程内托管的桌宠存进启动器设置（这样设置页和桌宠菜单改的是同一个值），
    /// 独立版存进 <see cref="PetSettings"/>。至于这条后端在不在、能不能用，由视图层判断 ——
    /// 目前只有 Windows(WPF) 侧有（<c>Viewport3D</c>），Avalonia 侧取到 true 也只会忽略。</para>
    ///
    /// <para>改动时应当触发 <see cref="PropertyChanged"/> 的 <c>UseGpu</c> 通知，好让已经开着的
    /// 桌宠窗口在设置页一改就跟着换后端。</para>
    /// </summary>
    bool UseGpu { get; set; }

    /// <summary>Brings the launcher to the front.</summary>
    void OpenLauncher();

    /// <summary>
    /// Raised with <see cref="EffectiveName"/> when the inherited name changes (e.g. the user
    /// switches accounts in the launcher). Kept separate from <see cref="CustomName"/> changes,
    /// which the pet itself originates and therefore already knows about.
    /// </summary>
    event PropertyChangedEventHandler? PropertyChanged;
}
