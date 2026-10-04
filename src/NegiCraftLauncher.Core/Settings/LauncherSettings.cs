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

    /// <summary>
    ///     视频壁纸。与 <see cref="CustomBackgroundPath" /> 互斥 —— 两个都设了就按视频算。
    ///
    ///     <para>只有 Windows(WPF) 侧会播它：Avalonia 侧没有视频面，读到非空值等于没有背景，
    ///     会回落到生成图。所以这个字段跨平台共享，语义是"用户在这台机器上选了这段视频当背景"，
    ///     而不是"背景一定是视频"。</para>
    /// </summary>
    public string? VideoBackgroundPath { get; set; }

    public string? CurrentInstanceId { get; set; }

    /// <summary>桌宠独立自定义名称；为 null 或空时继承当前主账号名称。</summary>
    public string? PetCustomName { get; set; }

    /// <summary>
    ///     桌宠是否用 GPU 硬件渲染。
    ///
    ///     <para>只有 Windows(WPF) 侧有这条后端 —— 那边是把模型塞进 <c>Viewport3D</c> 交给显卡；
    ///     Avalonia 侧本来就走 OpenGL，读到了也无处可用，直接忽略即可。所以这个字段跨平台共享、
    ///     语义是"我在这台机器上要不要开硬件后端"。</para>
    ///
    ///     <para>默认 false：软件光栅化是回归基线（确定性、可离屏出图），GPU 是可选加速项。</para>
    /// </summary>
    public bool PetUseGpu { get; set; }

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
