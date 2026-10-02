using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core.Versions;

/// <summary>
/// One version JSON. Loader profiles are the same shape plus <see cref="InheritsFrom"/>, and are
/// flattened onto their parent with <see cref="Merge"/>.
/// </summary>
public sealed class VersionProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("inheritsFrom")]
    public string? InheritsFrom { get; set; }

    /// <summary>Names the version whose client jar should be run; only loader profiles set it.</summary>
    [JsonPropertyName("jar")]
    public string? Jar { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "release";

    [JsonPropertyName("mainClass")]
    public string? MainClass { get; set; }

    /// <summary>Pre-1.13 argument template; modern profiles use <see cref="Arguments"/> instead.</summary>
    [JsonPropertyName("minecraftArguments")]
    public string? MinecraftArguments { get; set; }

    [JsonPropertyName("arguments")]
    public ArgumentsBlock? Arguments { get; set; }

    [JsonPropertyName("libraries")]
    public List<LibraryEntry> Libraries { get; set; } = new();

    [JsonPropertyName("assetIndex")]
    public AssetIndexEntry? AssetIndex { get; set; }

    [JsonPropertyName("assets")]
    public string? Assets { get; set; }

    [JsonPropertyName("downloads")]
    public DownloadsBlock? Downloads { get; set; }

    [JsonPropertyName("javaVersion")]
    public JavaVersionEntry? JavaVersion { get; set; }

    [JsonPropertyName("logging")]
    public Dictionary<string, LoggingEntry>? Logging { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTimeOffset? ReleaseTime { get; set; }

    [JsonPropertyName("minimumLauncherVersion")]
    public int MinimumLauncherVersion { get; set; }

    /// <summary>Modern profiles only; true means the argument arrays are present and authoritative.</summary>
    [JsonIgnore]
    public bool HasModernArguments => Arguments is not null;

    public static VersionProfile Parse(string json) =>
        JsonSerializer.Deserialize<VersionProfile>(json, NclJson.Options)
        ?? throw new InvalidDataException("版本 JSON 为空");

    public static VersionProfile Merge(VersionProfile parent, VersionProfile child)
    {
        var libraries = new List<LibraryEntry>(child.Libraries.Count + parent.Libraries.Count);
        libraries.AddRange(child.Libraries);
        libraries.AddRange(parent.Libraries);

        return new VersionProfile
        {
            Id = child.Id,
            InheritsFrom = null,
            // Forge-style profiles point at the vanilla jar; vanilla profiles leave this null.
            Jar = child.Jar ?? parent.Jar,
            Type = string.IsNullOrEmpty(parent.Type) ? child.Type : parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = MergeArguments(parent.Arguments, child.Arguments),
            Libraries = libraries,
            AssetIndex = parent.AssetIndex,
            Assets = parent.Assets,
            Downloads = parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = parent.Logging,
            ReleaseTime = parent.ReleaseTime ?? child.ReleaseTime,
            MinimumLauncherVersion = Math.Max(parent.MinimumLauncherVersion, child.MinimumLauncherVersion),
        };
    }

    private static ArgumentsBlock MergeArguments(ArgumentsBlock? parent, ArgumentsBlock? child)
    {
        if (parent is null) return child ?? new ArgumentsBlock();
        if (child is null) return parent;

        var game = new List<ArgumentValue>(parent.Game.Count + child.Game.Count);
        game.AddRange(parent.Game);
        game.AddRange(child.Game);

        // A loader that ships its own JVM arguments (Forge swaps -cp for -DlegacyClassPath and a
        // module path) must win outright, otherwise both sets get applied and the JVM refuses to start.
        var jvm = child.Jvm.Count > 0 ? child.Jvm : parent.Jvm;

        return new ArgumentsBlock { Game = game, Jvm = jvm };
    }
}

public sealed class ArgumentsBlock
{
    [JsonPropertyName("game")]
    public List<ArgumentValue> Game { get; set; } = new();

    [JsonPropertyName("jvm")]
    public List<ArgumentValue> Jvm { get; set; } = new();
}

/// <summary>
/// An argument is either a bare string or an object carrying rules plus one or more values.
/// </summary>
[JsonConverter(typeof(ArgumentValueConverter))]
public sealed class ArgumentValue
{
    public List<LaunchRule>? Rules { get; set; }

    public JsonElement Value { get; set; }

    public IEnumerable<string> Enumerate() => Value.ValueKind switch
    {
        JsonValueKind.String => new[] { Value.GetString()! },
        JsonValueKind.Array => Value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!),
        _ => Array.Empty<string>(),
    };
}

public sealed class ArgumentValueConverter : JsonConverter<ArgumentValue>
{
    public override ArgumentValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        // The bare-string form carries no rules, so it always applies.
        if (root.ValueKind == JsonValueKind.String)
        {
            return new ArgumentValue { Value = root.Clone() };
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("参数条目既不是字符串也不是对象");
        }

        var argument = new ArgumentValue();

        if (root.TryGetProperty("value", out var value))
        {
            argument.Value = value.Clone();
        }

        if (root.TryGetProperty("rules", out var rules))
        {
            argument.Rules = rules.Deserialize<List<LaunchRule>>(options);
        }

        return argument;
    }

    public override void Write(Utf8JsonWriter writer, ArgumentValue value, JsonSerializerOptions options)
    {
        if (value.Rules is null)
        {
            value.Value.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("rules");
        JsonSerializer.Serialize(writer, value.Rules, options);
        writer.WritePropertyName("value");
        value.Value.WriteTo(writer);
        writer.WriteEndObject();
    }
}

public sealed class LibraryEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("downloads")]
    public LibraryDownloads? Downloads { get; set; }

    [JsonPropertyName("rules")]
    public List<LaunchRule>? Rules { get; set; }

    /// <summary>Keyed by OS name; the value may contain <c>${arch}</c>.</summary>
    [JsonPropertyName("natives")]
    public Dictionary<string, string>? Natives { get; set; }

    [JsonPropertyName("extract")]
    public ExtractEntry? Extract { get; set; }

    /// <summary>Alternate maven root for libraries Mojang does not host.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>Set on libraries that only exist locally (installer-produced), never downloaded.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public sealed class LibraryDownloads
{
    [JsonPropertyName("artifact")]
    public ArtifactEntry? Artifact { get; set; }

    [JsonPropertyName("classifiers")]
    public Dictionary<string, ArtifactEntry>? Classifiers { get; set; }
}

public sealed class ArtifactEntry
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class ExtractEntry
{
    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = new();
}

public sealed class AssetIndexEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("totalSize")]
    public long TotalSize { get; set; }
}

public sealed class DownloadsBlock
{
    [JsonPropertyName("client")]
    public ArtifactEntry? Client { get; set; }

    [JsonPropertyName("client_mappings")]
    public ArtifactEntry? ClientMappings { get; set; }

    [JsonPropertyName("server")]
    public ArtifactEntry? Server { get; set; }
}

public sealed class JavaVersionEntry
{
    [JsonPropertyName("component")]
    public string Component { get; set; } = "";

    [JsonPropertyName("majorVersion")]
    public int MajorVersion { get; set; }
}

public sealed class LoggingEntry
{
    [JsonPropertyName("argument")]
    public string Argument { get; set; } = "";

    [JsonPropertyName("file")]
    public LoggingFileEntry File { get; set; } = new();

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed class LoggingFileEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha1")]
    public string? Sha1 { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }
}

public sealed class AssetIndex
{
    [JsonPropertyName("objects")]
    public Dictionary<string, AssetObject> Objects { get; set; } = new();

    public static AssetIndex Parse(string json) =>
        JsonSerializer.Deserialize<AssetIndex>(json, NclJson.Options)
        ?? throw new InvalidDataException("资源索引为空");
}

public sealed class AssetObject
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>objects/&lt;first two chars of hash&gt;/&lt;hash&gt;</summary>
    [JsonIgnore]
    public string RelativePath => Hash[..2] + "/" + Hash;
}
