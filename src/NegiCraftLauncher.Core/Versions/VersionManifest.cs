using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core.Versions;

public sealed class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions Latest { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<VersionSummary> Versions { get; set; } = new();

    public VersionSummary? Find(string id) =>
        Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class LatestVersions
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = "";

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = "";
}

public sealed class VersionSummary
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "release";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; set; }

    /// <summary>
    /// When the version actually shipped. The manifest's <c>time</c> is when Mojang last touched the
    /// JSON, which drifts years after release, so display dates come from here.
    /// </summary>
    [JsonPropertyName("releaseTime")]
    public DateTimeOffset ReleaseTime { get; set; }

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("complianceLevel")]
    public int ComplianceLevel { get; set; }

    public bool IsRelease => Type == "release";

    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        _ => Type,
    };

    public DateTimeOffset EffectiveDate => ReleaseTime != default ? ReleaseTime : Time;

    public string ReleaseDate => EffectiveDate.ToLocalTime().ToString("yyyy-MM-dd");

    public string Description => $"{TypeLabel} · {ReleaseDate}";

    public static VersionManifest Parse(string json) =>
        JsonSerializer.Deserialize<VersionManifest>(json, NclJson.Options)
        ?? throw new InvalidDataException("版本清单为空");
}
