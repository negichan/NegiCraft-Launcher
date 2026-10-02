using System;

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

    public string MetaText => $"{VersionId} · {Loader}";
}
