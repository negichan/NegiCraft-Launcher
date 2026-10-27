using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// Lists the loader builds that can be paired with a given Minecraft version. Same sources the
/// installer itself reads: Fabric's meta endpoint, and the two Maven metadata files for
/// Forge / NeoForge. Quilt and OptiFine are deliberately absent — the launcher cannot install them.
/// </summary>
public static class LoaderCatalog
{
    private const int MaxVersions = 60;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly ConditionalWeakTable<HttpClient, Dictionary<string, CacheEntry>> Caches = new();

    private sealed record CacheEntry(Task<string[]> Versions, DateTimeOffset ExpiresAt);

    public static Task<IReadOnlyList<string>> ListAsync(
        string kind, string minecraftVersion, IDownloadSource source, HttpClient http,
        CancellationToken ct = default) =>
        kind switch
        {
            "fabric" => FabricVersionsAsync(minecraftVersion, source, http, ct),
            "forge" => MavenVersionsAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
                minecraftVersion + "-", source, http, ct),
            "neoforge" => NeoForgeVersionsAsync(minecraftVersion, source, http, ct),
            _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
        };

    private static async Task<IReadOnlyList<string>> FabricVersionsAsync(
        string minecraftVersion, IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        return await CachedVersionsAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}",
            source, http, maven: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// NeoForge names builds <c>&lt;major&gt;.&lt;minor&gt;.&lt;build&gt;</c> where the first two
    /// numbers follow the Minecraft version (<c>1.21.11</c> → <c>21.11.x</c>). 1.20.1 and older
    /// were still Forge releases, so they simply match nothing here.
    /// </summary>
    private static Task<IReadOnlyList<string>> NeoForgeVersionsAsync(
        string minecraftVersion, IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        var parts = minecraftVersion.Split('.');
        if (parts.Length < 3 || parts[0] != "1")
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        return MavenVersionsAsync(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
            $"{parts[1]}.{parts[2]}.", source, http, ct, stripPrefix: false);
    }

    private static async Task<IReadOnlyList<string>> MavenVersionsAsync(
        string metadataUrl, string prefix, IDownloadSource source, HttpClient http, CancellationToken ct,
        bool stripPrefix = true)
    {
        var versions = await CachedVersionsAsync(metadataUrl, source, http, maven: true, ct)
            .ConfigureAwait(false);
        return versions
            .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(version => stripPrefix ? version[prefix.Length..] : version)
            .Reverse()
            .Take(MaxVersions)
            .ToArray();
    }

    private static async Task<string[]> CachedVersionsAsync(
        string url, IDownloadSource source, HttpClient http, bool maven, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var urls = source.Candidates(url).ToArray();
        var key = string.Join("\n", urls);
        var cache = Caches.GetValue(http, static _ => new());
        CacheEntry entry;
        lock (cache)
        {
            if (!cache.TryGetValue(key, out entry!) || entry.ExpiresAt <= DateTimeOffset.UtcNow ||
                entry.Versions.IsFaulted || entry.Versions.IsCanceled)
            {
                // The shared fetch outlives a cancelled UI selection. Parsing also stays off the UI thread.
                entry = new CacheEntry(Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var text = await HttpText.FetchAsync(http, urls, timeout.Token,
                        attemptTimeout: TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                    if (maven)
                        return XDocument.Parse(text).Descendants("version")
                            .Select(node => node.Value.Trim()).ToArray();

                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.EnumerateArray()
                        .Where(item => item.TryGetProperty("loader", out var loader) &&
                                       loader.TryGetProperty("version", out var version) &&
                                       version.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetProperty("loader").GetProperty("version").GetString()!)
                        .Where(version => version.Length > 0)
                        .Take(MaxVersions).ToArray();
                }), DateTimeOffset.UtcNow + CacheLifetime);
                cache[key] = entry;
            }
        }

        try
        {
            return await entry.Versions.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (entry.Versions.IsFaulted || entry.Versions.IsCanceled)
        {
            lock (cache)
            {
                if (cache.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                    cache.Remove(key);
            }
            throw;
        }
    }
}
