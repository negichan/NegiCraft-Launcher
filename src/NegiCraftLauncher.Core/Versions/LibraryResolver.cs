using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Versions;

public sealed record ResolvedLibrary(
    string MavenName,
    string RelativePath,
    string LocalPath,
    long Size,
    string? Sha1,
    bool IsNative,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> ExtractExcludes)
{
    /// <summary>group:artifact, used to drop a parent's older copy when a loader overrides it.</summary>
    public string Coordinate { get; init; } = "";    public bool NeedsDownload => Urls.Count > 0;
}

public sealed class LibraryResolution
{
    public required IReadOnlyList<ResolvedLibrary> Classpath { get; init; }
    public required IReadOnlyList<ResolvedLibrary> Natives { get; init; }

    public IEnumerable<ResolvedLibrary> All => Classpath.Concat(Natives);
}

public static class LibraryResolver
{
    private const string DefaultLibraryHost = "https://libraries.minecraft.net/";

    public static LibraryResolution Resolve(
        IEnumerable<LibraryEntry> libraries,
        string librariesRoot,
        IDownloadSource source,
        LaunchEnvironment? env = null)
    {
        env ??= LaunchEnvironment.Current;

        var classpath = new List<ResolvedLibrary>();
        var natives = new List<ResolvedLibrary>();
        var seenCoordinates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries)
        {
            if (!RuleEvaluator.Allows(library.Rules, env)) continue;
            if (string.IsNullOrWhiteSpace(library.Name)) continue;

            var nativeClassifier = ResolveNativeClassifier(library, env);
            var artifact = nativeClassifier is { } native
                ? library.Downloads?.Classifiers?.GetValueOrDefault(native)
                : library.Downloads?.Artifact;

            // A library that declares natives for this OS but ships no classifier artifact here is
            // simply not applicable; adding it would put a missing jar on the classpath.
            if (nativeClassifier is not null && artifact is null) continue;

            var relativePath = NormaliseSeparators(
                artifact?.Path ?? library.Path ?? BuildRelativePath(library.Name, nativeClassifier));

            var resolved = new ResolvedLibrary(
                MavenName: library.Name,
                RelativePath: relativePath,
                LocalPath: Path.Combine(librariesRoot, relativePath),
                Size: artifact?.Size ?? 0,
                Sha1: artifact?.Sha1,
                IsNative: nativeClassifier is not null,
                Urls: BuildUrls(artifact, library, relativePath, source),
                ExtractExcludes: library.Extract?.Exclude ?? new List<string>())
            {
                Coordinate = CoordinateOf(library.Name),
            };

            if (resolved.IsNative)
            {
                natives.Add(resolved);
                continue;
            }

            // Forge ships a newer asm than vanilla; whichever appears first on the classpath wins, so
            // the loader's copy (merged ahead of the parent's) must not be displaced by the original.
            if (seenCoordinates.Add(resolved.Coordinate))
            {
                classpath.Add(resolved);
            }
        }

        return new LibraryResolution { Classpath = classpath, Natives = natives };
    }

    private static IReadOnlyList<string> BuildUrls(
        ArtifactEntry? artifact, LibraryEntry library, string relativePath, IDownloadSource source)
    {
        // No artifact and no custom maven root means the file only ever exists locally.
        if (artifact is null || string.IsNullOrEmpty(artifact.Url))
        {
            if (string.IsNullOrEmpty(library.Url)) return Array.Empty<string>();
            return source.Candidates(library.Url!.TrimEnd('/') + "/" + relativePath);
        }

        var rawUrl = !string.IsNullOrEmpty(artifact.Url)
            ? artifact.Url
            : DefaultLibraryHost + relativePath;

        return source.Candidates(rawUrl);
    }

    private static string? ResolveNativeClassifier(LibraryEntry library, LaunchEnvironment env)
    {
        if (library.Natives is not { Count: > 0 } natives) return null;
        if (!natives.TryGetValue(env.OsName, out var template)) return null;

        // ${arch} expands to the JVM's bitness, not the OS's.
        return template.Replace("${arch}", env.Is64Bit ? "64" : "32");
    }

    /// <summary>group:artifact:version[:classifier] → group/artifact/version/artifact-version[-classifier].jar</summary>
    public static string BuildRelativePath(string mavenName, string? classifier = null)
    {
        var parts = mavenName.Split(':');
        if (parts.Length < 3) return mavenName + ".jar";

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var suffix = classifier ?? (parts.Length > 3 ? parts[3] : null);

        var fileName = string.IsNullOrEmpty(suffix)
            ? $"{artifact}-{version}.jar"
            : $"{artifact}-{version}-{suffix}.jar";

        return $"{group}/{artifact}/{version}/{fileName}";
    }

    /// <summary>
    /// group:artifact[:classifier] — the classifier must stay part of the key, because a native jar
    /// and its plain jar share group:artifact and both belong on the classpath.
    /// </summary>
    public static string CoordinateOf(string mavenName)
    {
        var parts = mavenName.Split(':');
        return parts.Length switch
        {
            >= 4 => $"{parts[0]}:{parts[1]}:{parts[3]}",
            >= 2 => $"{parts[0]}:{parts[1]}",
            _ => mavenName,
        };
    }

    private static string NormaliseSeparators(string path) => path.Replace('\\', '/').TrimStart('/');
}
