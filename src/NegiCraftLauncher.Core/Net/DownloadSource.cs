using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Settings;

namespace NegiCraftLauncher.Core.Net;

/// <summary>
/// Maps the absolute URLs found inside Mojang's metadata onto a mirror.
/// </summary>
public interface IDownloadSource
{
    string DisplayName { get; }
    string VersionManifestUrl { get; }

    /// <summary>Rewrite one metadata-provided URL for this source.</summary>
    string Map(string url);

    /// <summary>Urls to try in order: the mirror first, Mojang as a safety net.</summary>
    IReadOnlyList<string> Candidates(string url);
}

public sealed class OfficialDownloadSource : IDownloadSource
{
    public static readonly OfficialDownloadSource Instance = new();

    public string DisplayName => "官方";

    public string VersionManifestUrl => "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public string Map(string url) => url;

    public IReadOnlyList<string> Candidates(string url) => new[] { url };
}

/// <summary>
/// BMCLAPI is a community mirror sponsored by bangbang93; the launcher must credit it wherever the
/// source is selectable.
/// </summary>
public sealed class BmclapiDownloadSource : IDownloadSource
{
    public const string Host = "bmclapi2.bangbang93.com";

    public static readonly BmclapiDownloadSource Instance = new();

    private static readonly Dictionary<string, string> HostMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["launchermeta.mojang.com"] = Host,
        ["launcher.mojang.com"] = Host,
        ["piston-meta.mojang.com"] = Host,
        ["piston-data.mojang.com"] = Host,
        ["sessionserver.mojang.com"] = Host,
        ["authserver.mojang.com"] = Host,
        ["resources.download.minecraft.net"] = Host + "/assets",
        ["libraries.minecraft.net"] = Host + "/maven",
        ["maven.minecraftforge.net"] = Host + "/maven",
        ["files.minecraftforge.net"] = Host + "/maven",
        ["maven.neoforged.net"] = Host + "/maven",
        ["meta.fabricmc.net"] = Host + "/fabric-meta",
        ["maven.fabricmc.net"] = Host + "/maven",
        ["maven.quiltmc.org"] = Host + "/maven",
    };

    public string DisplayName => "BMCLAPI";

    public string VersionManifestUrl => $"https://{Host}/mc/game/version_manifest_v2.json";

    public string Map(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        if (!HostMap.TryGetValue(uri.Host, out var replacement)) return url;

        var builder = new UriBuilder(uri) { Host = Host, Port = -1 };
        var suffix = replacement.Contains('/') ? replacement[(replacement.IndexOf('/') + 1)..] : "";
        builder.Path = string.IsNullOrEmpty(suffix) ? uri.AbsolutePath : "/" + suffix + uri.AbsolutePath;
        return builder.Uri.ToString();
    }

    public IReadOnlyList<string> Candidates(string url)
    {
        var mapped = Map(url);
        return mapped == url ? new[] { url } : new[] { mapped, url };
    }
}

public static class DownloadSources
{
    public static IDownloadSource Resolve(DownloadSource source) => source switch
    {
        DownloadSource.Official => OfficialDownloadSource.Instance,
        DownloadSource.Bmclapi => BmclapiDownloadSource.Instance,
        _ => AutoDownloadSource.Current,
    };

    public static IEnumerable<(DownloadSource Value, string Label)> Selectable { get; } = new[]
    {
        (DownloadSource.Official, OfficialDownloadSource.Instance.DisplayName),
        (DownloadSource.Bmclapi, BmclapiDownloadSource.Instance.DisplayName),
        (DownloadSource.Auto, "自动"),
    };
}

/// <summary>
/// "自动" resolves once per run by timing the version manifest on each source, then behaves like the winner.
/// </summary>
public sealed class AutoDownloadSource : IDownloadSource
{
    private static readonly Lazy<AutoDownloadSource> LazyInstance = new(() => new AutoDownloadSource());
    public static AutoDownloadSource Current => LazyInstance.Value;

    private IDownloadSource? _winner;

    public IDownloadSource Winner => _winner ??= OfficialDownloadSource.Instance;

    public void SetWinner(IDownloadSource source) => _winner = source;

    public string DisplayName => Winner.DisplayName;
    public string VersionManifestUrl => Winner.VersionManifestUrl;
    public string Map(string url) => Winner.Map(url);
    public IReadOnlyList<string> Candidates(string url) => Winner.Candidates(url);

    public static async Task<IDownloadSource> ProbeAsync(HttpClient http, CancellationToken ct = default)
    {
        var candidates = new IDownloadSource[] { OfficialDownloadSource.Instance, BmclapiDownloadSource.Instance };
        var timings = await Task.WhenAll(candidates.Select(async source =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await http.GetAsync(source.VersionManifestUrl,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                return response.IsSuccessStatusCode ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : double.MaxValue;
            }
            catch (Exception)
            {
                return double.MaxValue;
            }
        })).ConfigureAwait(false);

        var bestIndex = 0;
        for (var i = 1; i < timings.Length; i++)
        {
            if (timings[i] < timings[bestIndex]) bestIndex = i;
        }

        var best = candidates[bestIndex];
        Current.SetWinner(best);
        return best;
    }
}
