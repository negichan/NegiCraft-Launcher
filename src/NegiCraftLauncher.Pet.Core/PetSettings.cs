using System;
using System.IO;
using System.Text.Json;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// Name storage for the standalone pet.
///
/// The launcher-embedded pet keeps its name in the launcher's own settings.json (via
/// <see cref="IPetHost"/>), because there the name belongs to the launcher. The standalone build
/// has no launcher to own it, so it keeps <c>%APPDATA%\NCL\pet.json</c> instead. The two stores are
/// deliberately independent — nothing syncs them.
/// </summary>
public sealed class PetSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>User-chosen name; null means "use the default name".</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// 桌宠是否用 GPU 硬件渲染（WPF <c>Viewport3D</c> 后端）。
    ///
    /// <para>只有 Windows 侧有这条后端；Avalonia 侧本来就走 OpenGL，读到 true 也无处可用。
    /// 默认 false —— 软件光栅化是回归基线，GPU 是可选加速项。</para>
    /// </summary>
    public bool UseGpu { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCL", "pet.json");

    /// <summary>Reads the settings, falling back to defaults when absent or unreadable.</summary>
    public static PetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(FilePath), JsonOptions)
                       ?? new PetSettings();
            }
        }
        catch
        {
            // A corrupt or unreadable file must not stop the pet from starting.
        }

        return new PetSettings();
    }

    /// <summary>Best-effort persist; failures are non-fatal.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
        }
    }
}
