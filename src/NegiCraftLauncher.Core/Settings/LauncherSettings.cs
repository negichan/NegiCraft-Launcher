using System;
using System.IO;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core.Settings;

public enum DownloadSource
{
    Official,
    Bmclapi,
    /// <summary>Probe both and keep whichever answered faster for this run.</summary>
    Auto,
}

/// <summary>
/// Everything the settings page edits, persisted as a single JSON document.
/// </summary>
public sealed class LauncherSettings
{
    public string GameRoot { get; set; } = NclPaths.DefaultGameRoot;

    /// <summary>Explicit javaw.exe; null means pick one automatically per version.</summary>
    public string? JavaPath { get; set; }

    public int MaxMemoryMb { get; set; } = 4096;

    public DownloadSource DownloadSource { get; set; } = DownloadSource.Auto;

    /// <summary>
    ///     Open download connections: shared across files, and a single large file is split into
    ///     at most this many ranged segments.
    /// </summary>
    public int DownloadThreads { get; set; } = 16;

    /// <summary>When true each instance gets its own saves/mods/config under versions/&lt;id&gt;/.</summary>
    public bool VersionIsolation { get; set; } = true;

    public bool HideOnLaunch { get; set; }

    public bool IsDark { get; set; } = true;

    public string? CustomBackgroundPath { get; set; }
    public double BackgroundBlur { get; set; }
    public double BackgroundBrightness { get; set; }

    public string? CurrentInstanceId { get; set; }

    public string EffectiveGameRoot =>
        string.IsNullOrWhiteSpace(GameRoot) ? NclPaths.DefaultGameRoot : GameRoot;

    [JsonIgnore]
    public string FilePath { get; private set; } = NclPaths.SettingsFile;

    public static LauncherSettings Load(string? path = null)
    {
        path ??= NclPaths.SettingsFile;
        LauncherSettings? loaded = null;
        if (File.Exists(path))
        {
            try
            {
                loaded = NclJson.Deserialize<LauncherSettings>(File.ReadAllText(path));
            }
            // A corrupt settings file must not stop the launcher from starting; fall back to defaults.
            catch (Exception)
            {
                loaded = null;
            }
        }

        var settings = loaded ?? new LauncherSettings();
        settings.FilePath = path;
        settings.Clamp();
        return settings;
    }

    public void Save()
    {
        Clamp();
        NclPaths.EnsureLauncherDirectories();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, NclJson.Serialize(this));
    }

    private void Clamp()
    {
        MaxMemoryMb = Math.Clamp(MaxMemoryMb, 512, 65536);
        DownloadThreads = Math.Clamp(DownloadThreads, 1, 64);
        BackgroundBlur = Math.Clamp(BackgroundBlur, 0, 40);
        BackgroundBrightness = Math.Clamp(BackgroundBrightness, -100, 100);
    }
}
