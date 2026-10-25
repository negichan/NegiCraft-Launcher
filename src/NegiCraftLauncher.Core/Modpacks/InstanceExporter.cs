using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>导出的结果：写进去多少个文件、总共多少字节，给提示那行用。</summary>
public sealed record InstanceExportResult(int FileCount, long TotalBytes);

/// <summary>
///     把一个实例打包成 Modrinth 的 <c>.mrpack</c>：<c>modrinth.index.json</c> 只写版本与加载器，
///     其余文件全放进 <c>overrides/</c>。这样不需要猜每个 jar 来自哪个平台 —— 贴过去的文件原样回来，
///     而且 <see cref="ModpackParser"/> 已经认这种包，导出后能直接再导入。
///
///     <para><b>不打包的东西</b>：版本本体（json / jar / natives）、日志与崩溃报告、存档与截图、
///     启动器自己的元数据。前两类重下就有，后两类是个人数据 —— 整合包是拿去分享的，不该揣着别人的世界。</para>
/// </summary>
public static class InstanceExporter
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly HashSet<string> SharedRootExcludedFolders = new(NameComparer)
    {
        "versions", "libraries", "assets", "runtime",
        "logs", "crash-reports", "natives",
        "saves", "screenshots",
    };

    private static readonly HashSet<string> InstanceExcludedFolders = new(NameComparer)
    {
        "logs", "crash-reports", "natives",
        "saves", "screenshots",
    };

    private static readonly HashSet<string> SharedRootExcludedFiles = new(NameComparer)
    {
        "launcher_profiles.json",
        "launcher_accounts.json",
        "launcher_accounts_microsoft_store.json",
        "launcher_settings.json",
        "usercache.json",
        "icon.png",
    };

    /// <summary>已经是压缩格式的资源，再压一遍白费 CPU，直接 store。</summary>
    private static readonly HashSet<string> StoredExtensions = new(NameComparer)
    {
        ".jar", ".zip", ".mrpack", ".7z", ".rar", ".gz", ".xz",
        ".png", ".jpg", ".jpeg", ".webp", ".gif",
        ".ogg", ".mp3", ".mp4", ".webm",
        ".mca", ".mcr",
    };

    private static readonly HashSet<string> JunkFiles = new(NameComparer)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini",
    };

    public static Task<InstanceExportResult> ExportAsync(
        InstanceManager instances,
        Instance instance,
        string targetPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(instance);
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("没有选择导出位置。", nameof(targetPath));

        // 压缩是同步 IO，放到线程池里跑，UI 只收进度。
        return Task.Run(() => Export(instances, instance, targetPath, progress, ct), ct);
    }

    private static InstanceExportResult Export(
        InstanceManager instances,
        Instance instance,
        string targetPath,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var source = instances.GameDirectoryFor(instance);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"实例目录不存在：{source}");

        var resolved = instances.Repository.Load(instance.Id);
        var minecraft = ResolveMinecraftVersion(instance, resolved);
        var loader = ResolveLoader(instance, resolved);

        var target = Path.GetFullPath(targetPath);
        var temp = target + ".part";

        progress?.Report(new InstallProgress("扫描实例文件", 0));
        var files = CollectFiles(source, instance, target, temp, ct);
        if (files.Count == 0) throw new InvalidDataException("这个实例没有可导出的文件。");

        var totalBytes = files.Sum(f => f.Size);
        var indexJson = BuildIndexJson(instance, minecraft, loader);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            WriteArchive(temp, indexJson, files, totalBytes, progress, ct);
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception)
        {
            TryDelete(temp);
            throw;
        }

        progress?.Report(new InstallProgress("导出完成", 1, $"{files.Count} 个文件 · {FormatSize(totalBytes)}"));
        return new InstanceExportResult(files.Count, totalBytes);
    }

    private static void WriteArchive(
        string path,
        string indexJson,
        List<ExportFile> files,
        long totalBytes,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var done = 0L;
        var watch = Stopwatch.StartNew();
        var buffer = new byte[128 * 1024];

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var index = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(index.Open(), new UTF8Encoding(false)))
        {
            writer.Write(indexJson);
        }

        progress?.Report(new InstallProgress("打包实例", 0, $"{files.Count} 个文件"));

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var entry = zip.CreateEntry("overrides/" + file.RelativePath, CompressionFor(file.RelativePath));
            entry.LastWriteTime = ClampStamp(file.LastWriteTime);

            // 游戏可能还开着，日志/崩溃报告已排除，剩下的用 ReadWrite 共享着读，
            // 免得别人正打开着某个配置文件就整次导出失败。
            using (var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = entry.Open())
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;

                    if (watch.ElapsedMilliseconds < 100) continue;
                    ReportFile(file.RelativePath, done, totalBytes, progress);
                    watch.Restart();
                }
            }

            // 小文件会一个接一个地过，不逐条上报：压缩整体都不到 100ms 时，
            // 几千次 UI 派发比压缩本身还贵。
            if (watch.ElapsedMilliseconds >= 100)
            {
                ReportFile(file.RelativePath, done, totalBytes, progress);
                watch.Restart();
            }
        }

        progress?.Report(new InstallProgress("打包实例", 0.99, "写入压缩包…"));
    }

    private static void ReportFile(
        string relativePath, long done, long totalBytes, IProgress<InstallProgress>? progress)
    {
        // 最后那 1% 留给「导出完成」—— 压缩包收尾时进度条不该先满上。
        var fraction = totalBytes > 0 ? Math.Min(0.99, (double)done / totalBytes) : 0.99;
        progress?.Report(new InstallProgress("打包实例", fraction, relativePath));
    }

    private static CompressionLevel CompressionFor(string relativePath) =>
        StoredExtensions.Contains(Path.GetExtension(relativePath))
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;

    private static DateTimeOffset ClampStamp(DateTime time)
    {
        if (time.Year < 1980) time = new DateTime(1980, 1, 1);
        if (time.Year > 2107) time = new DateTime(2107, 12, 31);
        return new DateTimeOffset(time);
    }

    private static string BuildIndexJson(Instance instance, string minecraft, DetectedLoader? loader)
    {
        var name = string.IsNullOrWhiteSpace(instance.DisplayName) ? instance.Id : instance.DisplayName.Trim();

        var dependencies = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft"] = minecraft,
        };

        if (loader is not null) dependencies[loader.DependencyKey] = loader.Version;

        var index = new ModrinthIndex
        {
            VersionId = DateTime.UtcNow.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture),
            Name = name,
            Dependencies = dependencies,
        };

        return NclJson.Serialize(index);
    }

    private static string ResolveMinecraftVersion(Instance instance, ResolvedVersion resolved)
    {
        if (!string.IsNullOrWhiteSpace(resolved.Profile.ClientVersion))
        {
            return resolved.Profile.ClientVersion!.Trim();
        }

        // Forge 老 profile 没继承链、也没 clientVersion 时，MC 版本写在加载器坐标里。
        if (FindLoader(resolved) is { MinecraftVersion: { Length: > 0 } fromLoader })
        {
            return fromLoader;
        }

        var root = resolved.RootVersionId;
        return string.IsNullOrWhiteSpace(root) ? instance.VersionId : root;
    }

    private static DetectedLoader? ResolveLoader(Instance instance, ResolvedVersion resolved)
    {
        var loader = FindLoader(resolved);

        if (loader is null && instance.Loader.StartsWith("OptiFine", StringComparison.OrdinalIgnoreCase))
        {
            // OptiFine 不是独立加载器，导入端也没有自动安装它的办法；悄悄导出成一个原版包
            // 会让接收方少一整层渲染补丁，不如当场说清楚。
            throw new NotSupportedException("OptiFine 实例还不能导出成整合包。");
        }

        return loader;
    }

    private static DetectedLoader? FindLoader(ResolvedVersion resolved)
    {
        foreach (var name in resolved.Profile.Libraries.Select(l => l.Name))
        {
            if (name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))
            {
                return Loader("fabric-loader", name);
            }

            if (name.StartsWith("org.quiltmc:quilt-loader:", StringComparison.OrdinalIgnoreCase))
            {
                return Loader("quilt-loader", name);
            }

            if (name.StartsWith("net.neoforged:neoforge:", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("net.neoforged:forge:", StringComparison.OrdinalIgnoreCase))
            {
                return Loader("neoforge", name);
            }

            if (name.StartsWith("net.minecraftforge:forge:", StringComparison.OrdinalIgnoreCase))
            {
                var coordinate = Part(name, 2);
                var dash = coordinate.IndexOf('-');

                // Forge 的坐标把游戏版本写进去了（1.16.5-36.2.20），依赖里只写加载器版本。
                return dash > 0
                    ? new DetectedLoader("forge", coordinate[(dash + 1)..], coordinate[..dash])
                    : new DetectedLoader("forge", coordinate, null);
            }
        }

        return null;

        static DetectedLoader Loader(string kind, string coordinate)
        {
            var version = Part(coordinate, 2);
            if (version.Length == 0) throw new InvalidDataException($"加载器坐标不完整：{coordinate}");
            return new DetectedLoader(kind, version, null);
        }
    }

    private static string Part(string coordinate, int index)
    {
        var parts = coordinate.Split(':');
        return parts.Length > index ? parts[index] : "";
    }

    /// <summary>
    ///     找出要装进 <c>overrides/</c> 的文件。隔离实例只读自己那一份版本目录；共用目录的实例
    ///     读整个游戏根，但把版本、库、资源与个人数据排除掉。
    /// </summary>
    private static List<ExportFile> CollectFiles(
        string root,
        Instance instance,
        string target,
        string temp,
        CancellationToken ct)
    {
        var files = new List<ExportFile>();
        var shared = !instance.Isolated;
        var excludedFolders = shared ? SharedRootExcludedFolders : InstanceExcludedFolders;
        var versionJson = instance.Id + ".json";
        var versionJar = instance.Id + ".jar";

        Walk(root, "");
        return files;

        void Walk(string directory, string relativeDirectory)
        {
            ct.ThrowIfCancellationRequested();

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception)
            {
                // 没权限/被占用的子目录跳过，不该为它让整个导出失败。
                return;
            }

            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();

                var name = Path.GetFileName(path);
                var relative = relativeDirectory.Length == 0 ? name : relativeDirectory + "/" + name;

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // 目录联接/符号链接不跟：可能绕到实例外面，也可能绕成环。
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (relativeDirectory.Length == 0 && IsExcludedFolder(name, shared, excludedFolders)) continue;

                    Walk(path, relative);
                    continue;
                }

                if (JunkFiles.Contains(name)) continue;

                if (relativeDirectory.Length == 0)
                {
                    if (shared && SharedRootExcludedFiles.Contains(name)) continue;
                    if (!shared && NameComparer.Equals(name, "icon.png")) continue;
                    if (!shared && NameComparer.Equals(name, versionJson)) continue;
                    if (!shared && NameComparer.Equals(name, versionJar)) continue;
                }

                var full = Path.GetFullPath(path);
                if (PathComparer.Equals(full, target) || PathComparer.Equals(full, temp)) continue;

                long size;
                DateTime lastWrite;
                try
                {
                    var info = new FileInfo(path);
                    size = info.Length;
                    lastWrite = info.LastWriteTime;
                }
                catch (Exception)
                {
                    continue;
                }

                files.Add(new ExportFile(full, relative, size, lastWrite));
            }
        }
    }

    private static bool IsExcludedFolder(string name, bool shared, HashSet<string> excluded)
    {
        if (excluded.Contains(name)) return true;

        // 共用根目录下整个 versions/ 都排除了；隔离实例里偶尔会有 <id>-natives 这种老 Forge 目录。
        return !shared && name.EndsWith("-natives", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB",
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // .part 删不掉只是留个垃圾，不能盖住真正的导出错误。
        }
    }

    private sealed record ExportFile(string FullPath, string RelativePath, long Size, DateTime LastWriteTime);

    /// <summary>Forge 的坐标里带着 MC 版本，只在没有 clientVersion 时拿来兜底。</summary>
    private sealed record DetectedLoader(string Kind, string Version, string? MinecraftVersion)
    {
        /// <summary>Modrinth 的依赖键：Fabric 用连字符，Quilt 用下划线，别写反。</summary>
        public string DependencyKey => Kind == "quilt-loader" ? "quilt_loader" : Kind;
    }

    private sealed class ModrinthIndex
    {
        [JsonPropertyName("formatVersion")]
        public int FormatVersion { get; } = 1;

        [JsonPropertyName("game")]
        public string Game { get; } = "minecraft";

        [JsonPropertyName("versionId")]
        public string VersionId { get; init; } = "1.0.0";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("files")]
        public object Files { get; } = Array.Empty<object>();

        [JsonPropertyName("dependencies")]
        public Dictionary<string, string> Dependencies { get; init; } = new();
    }
}
