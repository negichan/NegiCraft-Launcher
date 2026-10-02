using System;
using System.Collections.Generic;
using System.IO;

namespace NegiCraftLauncher.Core.Versions;

/// <summary>
/// A version with its inheritance chain flattened, plus the on-disk locations derived from it.
/// </summary>
public sealed class ResolvedVersion
{
    public required string GameRoot { get; init; }
    public required string InstanceId { get; init; }
    public required VersionProfile Profile { get; init; }

    /// <summary>The vanilla version at the bottom of the inheritance chain.</summary>
    public required string RootVersionId { get; init; }

    public string VersionDirectory => NclPaths.InstanceDirectory(GameRoot, InstanceId);
    public string ProfilePath => NclPaths.InstanceVersionJson(GameRoot, InstanceId);
    public string LibrariesRoot => NclPaths.LibrariesDirectory(GameRoot);
    public string AssetsRoot => NclPaths.AssetsDirectory(GameRoot);

    /// <summary>Loader profiles name the vanilla jar they run via the "jar" field.</summary>
    public string ClientJarVersionId => Profile.Jar ?? RootVersionId;

    public string ClientJarPath => Path.Combine(
        NclPaths.VersionsDirectory(GameRoot), ClientJarVersionId, ClientJarVersionId + ".jar");

    public string MainClass => string.IsNullOrEmpty(Profile.MainClass)
        ? throw new InvalidOperationException($"版本 {InstanceId} 没有声明 mainClass")
        : Profile.MainClass!;

    public string AssetIndexId => Profile.AssetIndex?.Id ?? Profile.Assets ?? "";

    public string AssetIndexPath => Path.Combine(NclPaths.AssetIndexesDirectory(GameRoot), AssetIndexId + ".json");

    public int RequiredJavaMajor
    {
        get
        {
            if (Profile.JavaVersion is { MajorVersion: > 0 } declared) return declared.MajorVersion;

            // Only versions from 1.17 onward declare javaVersion; anything older genuinely wants 8.
            // The date test exists purely as a guard against a hand-written profile omitting it.
            if (Profile.ReleaseTime is { } released)
            {
                if (released >= new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero)) return 21;
                if (released >= new DateTimeOffset(2021, 5, 1, 0, 0, 0, TimeSpan.Zero)) return 17;
            }

            return 8;
        }
    }
}

/// <summary>
/// Reads and writes version JSONs under the shared game root.
/// </summary>
public sealed class VersionRepository
{
    private const int MaxInheritanceDepth = 16;

    private readonly string _gameRoot;

    public VersionRepository(string gameRoot) => _gameRoot = gameRoot;

    public string DirectoryOf(string versionId) => NclPaths.InstanceDirectory(_gameRoot, versionId);

    public string ProfilePathOf(string versionId) => NclPaths.InstanceVersionJson(_gameRoot, versionId);

    public bool ProfileExists(string versionId) => File.Exists(ProfilePathOf(versionId));

    public bool ClientJarExists(string versionId)
    {
        var jar = Path.Combine(DirectoryOf(versionId), versionId + ".jar");
        return File.Exists(jar) && new FileInfo(jar).Length > 0;
    }

    public VersionProfile ReadProfile(string versionId)
    {
        var path = ProfilePathOf(versionId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到版本 {versionId} 的元数据", path);
        }

        return VersionProfile.Parse(File.ReadAllText(path));
    }

    public ResolvedVersion Load(string instanceId)
    {
        var chain = new List<VersionProfile>();
        var current = ReadProfile(instanceId);
        chain.Add(current);

        while (!string.IsNullOrEmpty(current.InheritsFrom))
        {
            if (chain.Count >= MaxInheritanceDepth)
            {
                throw new InvalidDataException($"版本 {instanceId} 的继承层级过深，可能已损坏");
            }

            current = ReadProfile(current.InheritsFrom!);
            chain.Add(current);
        }

        var merged = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            merged = VersionProfile.Merge(merged, chain[i]);
        }

        return new ResolvedVersion
        {
            GameRoot = _gameRoot,
            InstanceId = instanceId,
            Profile = merged,
            RootVersionId = chain[^1].Id,
        };
    }

    public ResolvedVersion Save(string instanceId, VersionProfile profile)
    {
        var directory = DirectoryOf(instanceId);
        NclPaths.EnsureDirectory(directory);

        profile.Id = instanceId;
        File.WriteAllText(ProfilePathOf(instanceId), NclJson.Serialize(profile));
        return Load(instanceId);
    }

    public void SaveRawJson(string instanceId, string json)
    {
        var directory = DirectoryOf(instanceId);
        NclPaths.EnsureDirectory(directory);
        File.WriteAllText(ProfilePathOf(instanceId), json);
    }
}
