using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Modrinth;

/// <summary>
/// Read-only access to the Modrinth API, which needs no key. Only search and version lookup are
/// implemented: those cover mods, resource packs and shaders, everything the resources page lists.
/// </summary>
public sealed class ModrinthClient
{
    private const string ApiRoot = "https://api.modrinth.com/v2";

    private readonly HttpClient _http;

    public ModrinthClient(HttpClient? http = null) => _http = http ?? NclHttp.Shared;

    /// <summary>Modrinth's project_type values that map onto the resources page tabs.</summary>
    public const string ProjectTypeMod = "mod";
    public const string ProjectTypeResourcePack = "resourcepack";
    public const string ProjectTypeShader = "shader";

    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string projectType,
        string? query = null,
        int limit = 20,
        int offset = 0,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var facets = new List<string> { $"[\"project_type:{projectType}\"]" };
        if (!string.IsNullOrWhiteSpace(gameVersion)) facets.Add($"[\"versions:{gameVersion}\"]");
        if (!string.IsNullOrWhiteSpace(loader)) facets.Add($"[\"categories:{loader}\"]");

        var url = ApiRoot + "/search?"
                  + "limit=" + limit.ToString(CultureInfo.InvariantCulture)
                  + "&offset=" + offset.ToString(CultureInfo.InvariantCulture)
                  + "&index=relevance"
                  + "&facets=" + Uri.EscapeDataString("[" + string.Join(",", facets) + "]");

        if (!string.IsNullOrWhiteSpace(query))
        {
            url += "&query=" + Uri.EscapeDataString(query.Trim());
        }

        var json = await HttpText.FetchAsync(_http, new[] { url }, ct).ConfigureAwait(false);
        var page = JsonSerializer.Deserialize<SearchPage>(json, NclJson.Options);

        return page?.Hits.Select(h => new ModrinthProject(
                h.ProjectId, h.Title, h.Description ?? "", h.Downloads, h.ProjectType))
            .ToList() ?? (IReadOnlyList<ModrinthProject>)Array.Empty<ModrinthProject>();
    }

    /// <summary>
    /// The newest published file that runs on the given game version and loader, or null when the
    /// project has nothing compatible.
    /// </summary>
    public async Task<ModrinthVersion?> FindVersionAsync(
        string projectId,
        string? gameVersion = null,
        string? loader = null,
        CancellationToken ct = default)
    {
        var url = ApiRoot + "/project/" + Uri.EscapeDataString(projectId) + "/version";

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            filters.Add("game_versions=" + Uri.EscapeDataString("[\"" + gameVersion + "\"]"));
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            filters.Add("loaders=" + Uri.EscapeDataString("[\"" + loader + "\"]"));
        }

        if (filters.Count > 0) url += "?" + string.Join("&", filters);

        var json = await HttpText.FetchAsync(_http, new[] { url }, ct).ConfigureAwait(false);
        var versions = JsonSerializer.Deserialize<List<VersionEntry>>(json, NclJson.Options);
        if (versions is null || versions.Count == 0) return null;

        // The API returns newest first; take the first entry that actually ships a file.
        var entry = versions.FirstOrDefault(v => v.Files.Count > 0);
        if (entry is null) return null;

        var primary = entry.Files.FirstOrDefault(f => f.Primary) ?? entry.Files[0];
        return new ModrinthVersion(entry.Id, entry.Name, primary.Url, primary.Filename,
            primary.Sha1, primary.Size);
    }

    private sealed class SearchPage
    {
        [JsonPropertyName("hits")]
        public List<SearchHit> Hits { get; set; } = new();

        [JsonPropertyName("total_hits")]
        public int TotalHits { get; set; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")]
        public string ProjectId { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("downloads")]
        public int Downloads { get; set; }

        [JsonPropertyName("project_type")]
        public string ProjectType { get; set; } = "";
    }

    private sealed class VersionEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("files")]
        public List<FileEntry> Files { get; set; } = new();
    }

    private sealed class FileEntry
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = "";

        [JsonPropertyName("sha1")]
        public string? Sha1 { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("primary")]
        public bool Primary { get; set; }
    }
}

public sealed record ModrinthProject(
    string Id, string Title, string Description, int Downloads, string ProjectType);

public sealed record ModrinthVersion(
    string Id, string Name, string Url, string FileName, string? Sha1, long Size)
{
    /// <summary>Keeps a hostile server from writing outside the target directory.</summary>
    public string SafeFileName
    {
        get
        {
            var name = Path.GetFileName(FileName.Replace('\\', '/'));
            foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? Id + ".jar" : name;
        }
    }
}
