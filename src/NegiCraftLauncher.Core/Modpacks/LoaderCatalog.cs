using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
        var json = await HttpText.FetchAsync(http,
            source.Candidates($"https://meta.fabricmc.net/v2/versions/loader/{minecraftVersion}"), ct)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        var versions = new List<string>();
        foreach (var entry in document.RootElement.EnumerateArray().Take(MaxVersions))
        {
            if (entry.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("version", out var version) &&
                version.GetString() is { Length: > 0 } value)
            {
                versions.Add(value);
            }
        }

        return versions;
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
            $"{parts[1]}.{parts[2]}.", source, http, ct);
    }

    private static async Task<IReadOnlyList<string>> MavenVersionsAsync(
        string metadataUrl, string prefix, IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        var xml = await HttpText.FetchAsync(http, source.Candidates(metadataUrl), ct).ConfigureAwait(false);

        var document = XDocument.Parse(xml);
        return document.Descendants("version")
            .Select(node => node.Value.Trim())
            .Where(version => version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(version => version[prefix.Length..])
            .Reverse()
            .Take(MaxVersions)
            .ToArray();
    }
}
