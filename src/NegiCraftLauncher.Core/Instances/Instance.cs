using System;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core.Instances;

/// <summary>
/// One playable installation, backed by a folder under <c>versions/</c>. The folder name is the
/// immutable id; everything else is launcher-side metadata that can be renamed freely.
/// </summary>
public sealed class Instance
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>The Minecraft version this instance was created from.</summary>
    public string VersionId { get; set; } = "";

    /// <summary>原版 / Fabric / Forge / NeoForge / OptiFine — display only for now.</summary>
    public string Loader { get; set; } = "原版";

    /// <summary>Per-instance overrides; null falls back to the global setting.</summary>
    public bool Isolated { get; set; } = true;

    public string? IconPath { get; set; }
    public string? JavaPath { get; set; }
    public int? MaxMemoryMb { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastPlayed { get; set; }

    /// <summary>
    ///     实例自己的一套 JVM 附加参数；空则只用全局的。卡片上的「⚙ → Java」编辑它。
    /// </summary>
    public string? ExtraJvmArguments { get; set; }

    /// <summary>
    ///     要不要 <c>-Xms</c> 直接申请到最大堆。<b>默认 false</b> —— 预分配省的是运行时的 GC
    ///     碎片，代价是启动就占住那么多物理内存，这该由用户决定而不是顺手替他开。
    /// </summary>
    public bool PreallocateMemory { get; set; }

    /// <summary>
    ///     mods/ 里现在有几个模组，只给卡片的「N 模组」标签用。
    ///     <b>每次扫描现算，不落盘</b> —— 用户会直接往 mods 里丢文件，存进元数据就是存一份会过期的谎。
    /// </summary>
    [JsonIgnore]
    public int ModCount { get; set; }

    public string MetaText => $"{VersionId} · {Loader}";
}
