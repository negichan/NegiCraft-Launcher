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
