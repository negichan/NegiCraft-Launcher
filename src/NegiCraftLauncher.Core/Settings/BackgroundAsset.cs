namespace NegiCraftLauncher.Core.Settings;

/// <summary>A reference to a user's wallpaper file, never an owned copy of the original.</summary>
public sealed class BackgroundAsset
{
    public string Path { get; set; } = "";
    public bool IsVideo { get; set; }
}
