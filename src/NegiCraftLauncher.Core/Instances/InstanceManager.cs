using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Instances;

/// <summary>
/// Instances are folders under <c>versions/</c>. Metadata that cannot live in the version JSON
/// (display name, per-instance overrides) is kept beside the launcher's settings, so a folder created
/// by another launcher is still picked up.
/// </summary>
public sealed class InstanceManager
{
    private static readonly char[] InvalidIdCharacters = Path.GetInvalidFileNameChars();

    private readonly string _gameRoot;
    private readonly string _metadataPath;
    private readonly List<Instance> _instances = new();
    private readonly VersionRepository _repository;

    public IReadOnlyList<Instance> Instances => _instances;

    public InstanceManager(string gameRoot, string? metadataPath = null)
    {
        _gameRoot = gameRoot;
        _metadataPath = metadataPath ?? NclPaths.InstancesFile;
        _repository = new VersionRepository(gameRoot);
    }

    public string GameRoot => _gameRoot;
    public VersionRepository Repository => _repository;

    public void Load()
    {
        _instances.Clear();

        var saved = ReadMetadata();

        foreach (var id in ScanFolders())
        {
            var existing = saved.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));
            _instances.Add(existing ?? Describe(id));
        }

        _instances.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));
    }

    public void Save()
    {
        var document = new MetadataDocument { Instances = _instances.ToList() };
        Directory.CreateDirectory(Path.GetDirectoryName(_metadataPath)!);
        File.WriteAllText(_metadataPath, NclJson.Serialize(document));
    }

    public Instance? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : _instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Registers an instance folder. Files are not downloaded here — see
    /// <see cref="VersionInstaller.InstallAsync"/>.
    /// </summary>
    /// <remarks>
    /// Without a display name the folder is the version id, so asking for the same version twice
    /// returns the same instance. A name makes the folder its own, which is what lets two instances
    /// run the same version with different saves.
    /// </remarks>
    public Instance Create(string versionId, string? displayName = null, string loader = "原版")
    {
        if (SanitizeId(versionId).Length == 0) throw new ArgumentException("版本 ID 不合法", nameof(versionId));

        if (string.IsNullOrWhiteSpace(displayName))
        {
            return Find(versionId) ?? Add(versionId, versionId, versionId, loader);
        }

        var name = displayName.Trim();
        var id = SanitizeId(name);
        if (id.Length == 0) id = versionId;

        return Add(UniqueId(id), name, versionId, loader);
    }

    private Instance Add(string id, string displayName, string versionId, string loader)
    {
        var instance = new Instance
        {
            Id = id,
            DisplayName = displayName,
            VersionId = versionId,
            Loader = loader,
            CreatedAt = DateTime.Now,
        };

        _instances.Add(instance);
        Save();
        return instance;
    }

    private string UniqueId(string id)
    {
        if (Find(id) is null && !_repository.ProfileExists(id)) return id;

        for (var n = 2; ; n++)
        {
            var candidate = $"{id} ({n})";
            if (Find(candidate) is null && !_repository.ProfileExists(candidate)) return candidate;
        }
    }

    public bool Rename(string id, string displayName)
    {
        var instance = Find(id);
        if (instance is null || string.IsNullOrWhiteSpace(displayName)) return false;

        instance.DisplayName = displayName.Trim();
        Save();
        return true;
    }

    public bool Delete(string id)
    {
        var instance = Find(id);
        if (instance is null) return false;

        _instances.Remove(instance);
        Save();

        var directory = _repository.DirectoryOf(id);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return true;
    }

    public void Touch(Instance instance)
    {
        instance.LastPlayed = DateTime.Now;
        Save();
    }

    /// <summary>Where the game reads and writes saves/mods/config for this instance.</summary>
    public string GameDirectoryFor(Instance instance) =>
        instance.Isolated ? _repository.DirectoryOf(instance.Id) : _gameRoot;

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && SanitizeId(id!) == id;

    public static string SanitizeId(string raw)
    {
        var cleaned = new string(raw.Trim().Where(c => !InvalidIdCharacters.Contains(c)).ToArray());
        return cleaned.TrimEnd('.', ' ');
    }

    private IEnumerable<string> ScanFolders()
    {
        var versionsRoot = NclPaths.VersionsDirectory(_gameRoot);
        if (!Directory.Exists(versionsRoot)) yield break;

        foreach (var directory in Directory.GetDirectories(versionsRoot))
        {
            var id = Path.GetFileName(directory);
            // A folder only counts as an instance once its version JSON has landed.
            if (_repository.ProfileExists(id)) yield return id;
        }
    }

    private Instance Describe(string id)
    {
        var instance = new Instance { Id = id, DisplayName = id, VersionId = id, CreatedAt = DirectoryCreationTime(id) };

        try
        {
            var raw = File.ReadAllText(_repository.ProfilePathOf(id));
            instance.Loader = DetectLoader(id, raw);

            var resolved = _repository.Load(id);
            instance.VersionId = resolved.RootVersionId;
        }
        catch (Exception)
        {
            // A half-written JSON still deserves a card so the user can delete it.
        }

        var icon = Path.Combine(_repository.DirectoryOf(id), "icon.png");
        if (File.Exists(icon)) instance.IconPath = icon;

        return instance;
    }

    private static string DetectLoader(string id, string json)
    {
        var haystack = id + "\n" + json;
        if (haystack.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
        if (haystack.Contains("fabric-loader", StringComparison.OrdinalIgnoreCase)) return "Fabric";
        if (haystack.Contains("quilt-loader", StringComparison.OrdinalIgnoreCase)) return "Quilt";
        if (haystack.Contains("optifine", StringComparison.OrdinalIgnoreCase)) return "OptiFine";
        if (haystack.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
        return "原版";
    }

    private DateTime DirectoryCreationTime(string id)
    {
        try
        {
            return Directory.GetCreationTime(_repository.DirectoryOf(id));
        }
        catch (Exception)
        {
            return DateTime.Now;
        }
    }

    private List<Instance> ReadMetadata()
    {
        try
        {
            if (!File.Exists(_metadataPath)) return new List<Instance>();
            return NclJson.Deserialize<MetadataDocument>(File.ReadAllText(_metadataPath))?.Instances
                   ?? new List<Instance>();
        }
        catch (Exception)
        {
            return new List<Instance>();
        }
    }

    private sealed class MetadataDocument
    {
        public List<Instance> Instances { get; set; } = new();
    }
}
