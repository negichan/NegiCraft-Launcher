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
///
/// <para>One manager per game root — the launcher can watch several <c>.minecraft</c> trees at once.
/// Metadata is scoped by that root inside <c>instances.json</c>, so two folders holding the same
/// version id are two different instances, and a folder the launcher never saw still gets a card.</para>
/// </summary>
public sealed class InstanceManager
{
    private static readonly char[] InvalidIdCharacters = Path.GetInvalidFileNameChars();

    /// <summary>mods/ 里算"一个模组"的后缀；禁用的模组常被改名成 <c>.jar.disabled</c>，那不算。</summary>
    private static readonly string[] ModFileExtensions = { ".jar", ".zip", ".litemod" };

    private readonly string _gameRoot;
    private readonly string _metadataPath;
    private readonly List<Instance> _instances = new();
    private readonly VersionRepository _repository;

    public IReadOnlyList<Instance> Instances => _instances;

    public InstanceManager(string gameRoot, string? metadataPath = null)
    {
        _gameRoot = NclPaths.NormaliseGameRoot(gameRoot);
        _metadataPath = metadataPath ?? NclPaths.InstancesFile;
        _repository = new VersionRepository(_gameRoot);
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
            var instance = existing ?? Describe(id);

            // 图标路径是「盘上有没有 icon.png」推出来的，不进 instances.json —— 所以已经存过的
            // 实例每次启动都得重算一遍。只在 Describe 里设的话，图标会永远停在实例第一次入库时
            // 的样子：后来换的图标不显示，删掉的图标也还挂着。
            AttachIconPath(instance);

            _instances.Add(instance);
        }

        _instances.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCulture));

        foreach (var instance in _instances)
        {
            instance.ModCount = CountMods(GameDirectoryFor(instance));
        }
    }

    public void Save()
    {
        var document = ReadDocument();

        var scoped = document.Folders.FirstOrDefault(f =>
            string.Equals(NclPaths.NormaliseGameRoot(f.Path), _gameRoot, StringComparison.OrdinalIgnoreCase));

        if (scoped is null)
        {
            scoped = new FolderMetadata { Path = _gameRoot };
            document.Folders.Add(scoped);
        }

        scoped.Instances = _instances.ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(_metadataPath)!);
        File.WriteAllText(_metadataPath, NclJson.Serialize(document));
    }

    /// <summary>
    ///     目录列表之前，<c>instances.json</c> 是一整份不分目录的元数据。第一次带多目录的运行把它整份挂到
    ///     <paramref name="gameRoot"/> 之下，之后就只按目录读写 —— 老文件里的自定义名字一个都不丢。
    /// </summary>
    public static void AdoptLegacyMetadata(string gameRoot, string? metadataPath = null)
    {
        metadataPath ??= NclPaths.InstancesFile;
        if (!File.Exists(metadataPath)) return;

        MetadataDocument? document;
        try
        {
            document = NclJson.Deserialize<MetadataDocument>(File.ReadAllText(metadataPath));
        }
        catch (Exception)
        {
            return;
        }

        if (document is null || document.Folders.Count > 0 || document.Instances.Count == 0) return;

        File.WriteAllText(metadataPath, NclJson.Serialize(new MetadataDocument
        {
            Folders =
            {
                new FolderMetadata
                {
                    Path = NclPaths.NormaliseGameRoot(gameRoot),
                    Instances = document.Instances,
                },
            },
        }));
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

    public bool Update(string id, string displayName, bool isolated, string? javaPath, int? maxMemoryMb,
        string? extraJvmArguments, bool preallocateMemory)
    {
        var instance = Find(id);
        if (instance is null || string.IsNullOrWhiteSpace(displayName)) return false;

        instance.DisplayName = displayName.Trim();
        instance.Isolated = isolated;
        instance.JavaPath = string.IsNullOrWhiteSpace(javaPath) ? null : javaPath.Trim();
        instance.MaxMemoryMb = maxMemoryMb;
        instance.ExtraJvmArguments = string.IsNullOrWhiteSpace(extraJvmArguments) ? null : extraJvmArguments.Trim();
        instance.PreallocateMemory = preallocateMemory;
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

    /// <summary>
    /// 实例自定义图标的位置 —— 实例自己目录下的 <c>icon.png</c>（不是游戏数据目录：
    /// 没开隔离时后者是整个根目录，把图标放那儿就成了所有实例共用一张）。
    ///
    /// <para>这个文件存在就优先用它，不存在才回落到加载器标志 / 方块，所以「恢复默认」
    /// 就是把它删掉，不需要任何额外的状态位。</para>
    /// </summary>
    public string IconPathFor(Instance instance) =>
        Path.Combine(_repository.DirectoryOf(instance.Id), "icon.png");

    /// <summary>
    /// 把预设方块设成实例图标，顺手清掉旧的 <c>icon.png</c> —— 两种来源只留一个，
    /// 免得「选过预设之后老的自选图还压在下面」。
    ///
    /// <para>写的是<b>键</b>（方块 <c>grass</c> / <c>anvil</c> …，或加载器标志 <c>loader:fabric</c>）不是图片：
    /// 图标本体在程序集里，复制一份到实例目录只是多占一份空间、多一次解码。</para>
    ///
    /// <para><paramref name="key"/> 传 <c>null</c> / 空白等于「恢复默认」。</para>
    /// </summary>
    public bool SetIconPreset(Instance instance, string? key)
    {
        var trimmed = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        if (trimmed is null) return ClearIcon(instance);

        try
        {
            DeleteIconFile(instance);
        }
        catch (Exception)
        {
            // 旧文件删不掉（被占用）不算失败：图标读的是 IconPreset，文件留着也不影响显示。
        }

        instance.IconPath = null;
        instance.IconPreset = trimmed;
        Save();
        return true;
    }

    /// <summary>清掉实例图标，回落到加载器标志 / 方块。本来就没有也算成功。</summary>
    public bool ClearIcon(Instance instance)
    {
        try
        {
            DeleteIconFile(instance);
        }
        catch (Exception)
        {
            return false;
        }

        instance.IconPath = null;
        instance.IconPreset = null;
        Save();
        return true;
    }

    private void DeleteIconFile(Instance instance)
    {
        var path = IconPathFor(instance);
        if (File.Exists(path)) File.Delete(path);
    }

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
            instance.Loader = WithLoaderVersion(instance.Loader, resolved);
        }
        catch (Exception)
        {
            // A half-written JSON still deserves a card so the user can delete it.
        }

        var icon = Path.Combine(_repository.DirectoryOf(id), "icon.png");
        if (File.Exists(icon)) instance.IconPath = icon;

        return instance;
    }

    /// <summary>
    ///     把 <see cref="IconPathFor"/> 那个位置上的文件挂成实例图标；没有就清空，让界面回落到
    ///     加载器标志 / 方块。文件是唯一状态，所以这里每次都按盘上的情况重算，不做缓存。
    /// </summary>
    private void AttachIconPath(Instance instance)
    {
        var icon = IconPathFor(instance);
        instance.IconPath = File.Exists(icon) ? icon : null;
    }

    /// <summary>
    ///     卡片的 tag 写「Fabric 0.16.10」这样的整串，加载器筛选按种类分组，所以版本号纯粹是显示 ——
    ///     库里没有那条坐标（比如只装了 OptiFine）就原样留着种类名。
    /// </summary>
    private static string WithLoaderVersion(string kind, ResolvedVersion resolved)
    {
        var coordinate = resolved.Profile.Libraries
            .Select(l => l.Name)
            .FirstOrDefault(n => n.Contains(":fabric-loader:", StringComparison.Ordinal) ||
                                 n.Contains(":quilt_loader:", StringComparison.Ordinal) ||
                                 n.Contains("neoforged:neoforge:", StringComparison.Ordinal) ||
                                 n.Contains("minecraftforge:forge:", StringComparison.Ordinal));

        if (coordinate is null) return kind;

        var parts = coordinate.Split(':');
        if (parts.Length < 3) return kind;

        var version = parts[2];
        var vanilla = resolved.RootVersionId + "-";
        if (version.StartsWith(vanilla, StringComparison.OrdinalIgnoreCase)) version = version[vanilla.Length..];

        return version.Length == 0 ? kind : $"{kind} {version}";
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

    private MetadataDocument ReadDocument()
    {
        try
        {
            if (!File.Exists(_metadataPath)) return new MetadataDocument();
            return NclJson.Deserialize<MetadataDocument>(File.ReadAllText(_metadataPath)) ?? new MetadataDocument();
        }
        catch (Exception)
        {
            return new MetadataDocument();
        }
    }

    private List<Instance> ReadMetadata() =>
        ReadDocument().Folders.FirstOrDefault(f => Owns(f.Path))?.Instances ?? new List<Instance>();

    private bool Owns(string? path) =>
        string.Equals(NclPaths.NormaliseGameRoot(path), _gameRoot, StringComparison.OrdinalIgnoreCase);

    private static int CountMods(string gameDirectory)
    {
        var mods = Path.Combine(gameDirectory, "mods");
        if (!Directory.Exists(mods)) return 0;

        try
        {
            return Directory.GetFiles(mods, "*", SearchOption.TopDirectoryOnly)
                .Count(file => ModFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private sealed class MetadataDocument
    {
        /// <summary>旧格式：不分目录的一整份，只由 <see cref="AdoptLegacyMetadata"/> 读一次。</summary>
        public List<Instance> Instances { get; set; } = new();

        public List<FolderMetadata> Folders { get; set; } = new();
    }

    private sealed class FolderMetadata
    {
        public string Path { get; set; } = "";
        public List<Instance> Instances { get; set; } = new();
    }
}
