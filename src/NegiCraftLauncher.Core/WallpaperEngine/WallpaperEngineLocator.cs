using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NegiCraftLauncher.Core.WallpaperEngine;

/// <summary>What kind of wallpaper Wallpaper Engine currently has selected.</summary>
public enum WallpaperEngineKind
{
    /// <summary>A plain video file — the only kind we can play as a video background.</summary>
    Video,

    /// <summary>A scene project (<c>scene.json</c> / <c>.pkg</c>) — needs the WE engine to render.</summary>
    Scene,

    /// <summary>A native application wallpaper (<c>.exe</c>).</summary>
    Application,

    /// <summary>A web wallpaper (<c>index.html</c>).</summary>
    Web,

    /// <summary>Selected file had an extension we do not recognise.</summary>
    Unknown,
}

/// <summary>
/// The wallpaper Wallpaper Engine currently has selected, plus the one thing this launcher can
/// actually do with it: play it as a video background (<see cref="VideoPath"/>).
///
/// <para>场景 / 网页 / 应用三类壁纸要靠 WE 自己的引擎渲染，我们渲染不了，也<b>不做</b>「拿预览图
/// 当静态背景」的兜底 —— 那等于把用户的动态壁纸悄悄换成一张截图。这几类一律只报类型、不改背景。</para>
/// </summary>
public sealed record WallpaperEngineWallpaper
{
    public required WallpaperEngineKind Kind { get; init; }

    /// <summary>
    /// The <c>file</c> entry from <c>config.json</c>, resolved to an absolute path.
    /// For a video this is the video itself; for a project it is the project's entry file.
    /// </summary>
    public required string SourcePath { get; init; }

    /// <summary>Set only when <see cref="Kind"/> is <see cref="WallpaperEngineKind.Video"/>.</summary>
    public string? VideoPath { get; init; }

    public string? Title { get; init; }

    /// <summary>Directory holding <c>project.json</c> (the folder Wallpaper Engine browses).</summary>
    public string? ProjectDirectory { get; init; }
}

/// <summary>
/// Reads the wallpaper Wallpaper Engine currently has selected, so the launcher can mirror it.
///
/// <para>Everything here is best-effort and silent: Wallpaper Engine may not be installed, may be
/// installed outside Steam, or may point at a project the user has since deleted. None of that is
/// an error worth surfacing — callers get <c>null</c> and decide what to say.</para>
///
/// <para>No Windows-only APIs (no registry, no P/Invoke): the shared <c>Core</c> project is compiled
/// for plain <c>net10.0</c> and linked by the Avalonia front end too. We locate the install either
/// from a running Wallpaper Engine process or by walking Steam's library folders.</para>
/// </summary>
public static class WallpaperEngineLocator
{
    private const string SteamAppFolder = "wallpaper_engine";

    private static readonly string[] VideoExtensions =
        [".mp4", ".webm", ".avi", ".mkv", ".mov", ".m4v", ".wmv", ".mpg", ".mpeg"];

    /// <summary>Wallpaper Engine's own process names (32- and 64-bit builds).</summary>
    private static readonly string[] ProcessNames = ["wallpaper64", "wallpaper32"];

    private static string? _cachedInstall;
    private static bool _installProbed;

    /// <summary>
    /// Absolute path of the Wallpaper Engine install directory, or <c>null</c> when we cannot find it.
    /// Cached after the first successful probe; pass <paramref name="refresh"/> to re-probe.
    /// </summary>
    public static string? FindInstallDirectory(bool refresh = false)
    {
        if (_installProbed && !refresh) return _cachedInstall;

        _cachedInstall = ProbeRunningProcess() ?? ProbeSteamLibraries();
        _installProbed = true;
        return _cachedInstall;
    }

    /// <summary>
    /// The wallpaper Wallpaper Engine currently has selected, or <c>null</c> when it is not installed,
    /// has no config, or the config names a wallpaper we cannot read.
    /// </summary>
    public static WallpaperEngineWallpaper? GetCurrent(bool refresh = false)
    {
        var install = FindInstallDirectory(refresh);
        if (string.IsNullOrEmpty(install)) return null;

        var selectedFile = ReadSelectedWallpaperFile(Path.Combine(install!, "config.json"));
        if (string.IsNullOrEmpty(selectedFile)) return null;

        var source = ResolvePath(selectedFile!, install!);
        if (source is null) return null;

        var projectDirectory = Path.GetDirectoryName(source);
        var project = ReadProject(projectDirectory);

        // project.json is authoritative; the extension is only a fallback for workshop items that
        // ship without one (and for `.pkg`, where the packed project's type lives inside the package).
        var kind = ParseKind(project?.Type) ?? KindFromExtension(source);

        var videoPath = kind == WallpaperEngineKind.Video && File.Exists(source) ? source : null;

        return new WallpaperEngineWallpaper
        {
            Kind = kind,
            SourcePath = source,
            VideoPath = videoPath,
            Title = string.IsNullOrWhiteSpace(project?.Title) ? Path.GetFileName(projectDirectory) : project!.Title,
            ProjectDirectory = projectDirectory,
        };
    }

    /// <summary>
    /// Wallpaper Engine runs from its install directory, so a live process is the most reliable
    /// pointer — it also covers installs Steam does not know about.
    /// </summary>
    private static string? ProbeRunningProcess()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        var exe = process.MainModule?.FileName;
                        if (string.IsNullOrEmpty(exe)) continue;

                        var directory = Path.GetDirectoryName(exe);
                        if (!string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory!, "config.json")))
                            return directory;
                    }
                }
            }
            catch (Exception)
            {
                // Access denied / process exited mid-enumeration: just try the next candidate.
            }
        }

        return null;
    }

    /// <summary>
    /// Walk Steam's library folders looking for <c>steamapps\common\wallpaper_engine</c>.
    ///
    /// <para>Note we deliberately <b>do not</b> trust the <c>apps</c> map inside
    /// <c>libraryfolders.vdf</c>: it is a cache Steam only refreshes when it rescans, and it is
    /// routinely missing apps that are in fact installed. Directory existence is the ground truth.</para>
    /// </summary>
    private static string? ProbeSteamLibraries()
    {
        foreach (var library in EnumerateLibraryRoots())
        {
            try
            {
                var candidate = Path.Combine(library, "steamapps", "common", SteamAppFolder);
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "config.json")))
                    return candidate;
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    /// <summary>Steam root folders plus every extra library folder they declare in <c>libraryfolders.vdf</c>.</summary>
    private static IEnumerable<string> EnumerateLibraryRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in EnumerateFixedDrives())
        {
            foreach (var relative in SteamRootCandidates())
            {
                var root = Path.Combine(drive, relative);
                if (!Directory.Exists(root)) continue;
                if (!seen.Add(root)) continue;

                yield return root;

                foreach (var extra in ReadLibraryFolders(root))
                {
                    if (seen.Add(extra)) yield return extra;
                }
            }
        }
    }

    private static IEnumerable<string> SteamRootCandidates()
    {
        yield return @"Program Files (x86)\Steam";
        yield return @"Program Files\Steam";
        yield return "Steam";
        yield return "SteamLibrary";
        yield return @"Games\Steam";
        yield return @"Games\SteamLibrary";
    }

    private static IEnumerable<string> EnumerateFixedDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            string name;
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                name = drive.Name;
            }
            catch (Exception)
            {
                continue;
            }

            yield return name;
        }
    }

    /// <summary>
    /// Pull every <c>"path" "..."</c> out of <c>steamapps\libraryfolders.vdf</c>. The file is Valve's
    /// KeyValues format, not JSON — a regex over the one key we need is enough and avoids a parser.
    /// </summary>
    private static IEnumerable<string> ReadLibraryFolders(string steamRoot)
    {
        string text;
        try
        {
            var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;
            text = File.ReadAllText(vdf);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     text, "\"path\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            var path = UnescapeVdf(match.Groups[1].Value);
            if (path.Length > 0 && Directory.Exists(path)) yield return path;
        }
    }

    private static string UnescapeVdf(string value) =>
        value.Replace("\\\\", "\\").Replace("\\\"", "\"");

    /// <summary>
    /// The <c>file</c> Wallpaper Engine has selected, for the primary monitor.
    ///
    /// <para>The config is keyed by Steam account name at the top level
    /// (<c>"Hanako": { "general": { "wallpaperconfig": { "selectedwallpapers": { "Monitor0": ... } } } }</c>),
    /// alongside non-account keys such as <c>?installdirectory</c> and <c>WsiAccount</c>. Rather than
    /// guess the account, we take the first top-level object that actually carries a
    /// <c>general.wallpaperconfig.selectedwallpapers</c> node.</para>
    /// </summary>
    private static string? ReadSelectedWallpaperFile(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return null;

            var text = File.ReadAllText(configPath);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var account in document.RootElement.EnumerateObject())
            {
                if (account.Name.StartsWith('?')) continue;
                if (account.Value.ValueKind != JsonValueKind.Object) continue;

                if (!account.Value.TryGetProperty("general", out var general)) continue;
                if (!general.TryGetProperty("wallpaperconfig", out var config)) continue;
                if (!config.TryGetProperty("selectedwallpapers", out var selected)) continue;
                if (selected.ValueKind != JsonValueKind.Object) continue;

                // Monitor0 first; otherwise whatever monitor the config happens to list first.
                if (!selected.TryGetProperty("Monitor0", out var monitor))
                    monitor = selected.EnumerateObject().Select(p => p.Value).FirstOrDefault();

                if (monitor.ValueKind == JsonValueKind.Object &&
                    monitor.TryGetProperty("file", out var file) &&
                    file.ValueKind == JsonValueKind.String)
                {
                    return file.GetString();
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private sealed record ProjectInfo(string? Type, string? Title);

    private static ProjectInfo? ReadProject(string? projectDirectory)
    {
        if (string.IsNullOrEmpty(projectDirectory)) return null;

        try
        {
            var manifest = Path.Combine(projectDirectory!, "project.json");
            if (!File.Exists(manifest)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var root = document.RootElement;
            return new ProjectInfo(
                StringOrNull(root, "type"),
                StringOrNull(root, "title"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? StringOrNull(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// config.json stores forward slashes even on Windows; both separators appear in the config's
    /// <c>file</c> value. Normalise to the platform separator before touching the disk.
    /// Relative values are resolved against <paramref name="baseDirectory"/>.
    /// </summary>
    private static string? ResolvePath(string raw, string baseDirectory)
    {
        try
        {
            var normalized = raw.Replace('\\', Path.DirectorySeparatorChar)
                                .Replace('/', Path.DirectorySeparatorChar);

            if (!Path.IsPathRooted(normalized))
                normalized = Path.Combine(baseDirectory, normalized);

            return Path.GetFullPath(normalized);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static WallpaperEngineKind KindFromExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return WallpaperEngineKind.Video;
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return WallpaperEngineKind.Application;
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            return WallpaperEngineKind.Web;
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            return WallpaperEngineKind.Scene;

        return WallpaperEngineKind.Unknown;
    }

    private static WallpaperEngineKind? ParseKind(string? type) => type?.Trim().ToLowerInvariant() switch
    {
        "video" => WallpaperEngineKind.Video,
        "scene" => WallpaperEngineKind.Scene,
        "application" => WallpaperEngineKind.Application,
        "web" => WallpaperEngineKind.Web,
        _ => null,
    };
}
