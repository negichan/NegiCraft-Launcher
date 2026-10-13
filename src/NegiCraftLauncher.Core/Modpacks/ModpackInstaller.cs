using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// 导入的结果： normally 一个新实例，普通压缩包那种"整份 .minecraft"则是一个新游戏目录的路径
/// （调用方把它登记进目录列表并切过去）。
/// </summary>
public sealed record ModpackImportResult(Instance? Instance, string? NewGameFolder, ModpackManifest Manifest);

/// <summary>
/// 把一份整合包落成能直接启动的东西：认格式 → 装原版 → 装加载器 → 补文件 → 下包里的 mod →
/// 铺覆盖层。每一步都往 <see cref="InstallProgress"/> 报，所以 UI 的进度条能对上真实工作量。
///
/// <para>下载全部走 <see cref="Downloader"/>，地址先过 <see cref="IDownloadSource"/> —— BMCLAPI
/// 镜像了 Mojang、Fabric、Forge、NeoForge 的 maven，所以国内导入不必翻墙；Modrinth 的 CDN 与
/// CurseForge 的 edge.forgecdn.net 它不镜像，映射会原样退回官方地址。</para>
/// </summary>
public sealed class ModpackInstaller
{
    private const double VanillaStart = 0.05;
    private const double VanillaEnd = 0.20;
    private const double LoaderStart = VanillaEnd;
    private const double FilesStart = 0.24;
    private const double FilesEnd = 0.80;
    private const double ModFilesStart = 0.80;
    private const double ModFilesEnd = 0.95;
    private const double OverridesStart = 0.95;

    private readonly LauncherSettings _settings;
    private readonly HttpClient _http;

    public ModpackInstaller(LauncherSettings settings, HttpClient? http = null)
    {
        _settings = settings;
        _http = http ?? NclHttp.Shared;
    }

    public async Task<ModpackImportResult> ImportAsync(
        InstanceManager instances,
        string archivePath,
        string? displayName,
        IDownloadSource source,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var pack = ModpackSource.Open(archivePath);
        var manifest = pack.ReadManifest();
        progress?.Report(new InstallProgress("识别整合包", 0, manifest.FormatLabel));

        if (manifest.Format == ModpackFormat.Generic)
        {
            return new ModpackImportResult(null, await ExtractAsGameFolder(pack, manifest, progress, ct).ConfigureAwait(false), manifest);
        }

        // Create 可能返回一个早就存在的同名实例（没起名字时就是那种），那一个不能当成"这次导入造的"来回收。
        var before = new HashSet<string>(
            instances.Instances.Select(i => i.Id), StringComparer.OrdinalIgnoreCase);

        var instance = instances.Create(
            manifest.MinecraftVersion,
            string.IsNullOrWhiteSpace(displayName) ? manifest.Name : displayName,
            LoaderLabel(manifest.Loader));

        // 覆盖层与 mod 都落在实例自己的目录里，所以隔离必须在算目录之前定下来。
        instance.Isolated = _settings.VersionIsolation;

        try
        {
            return await CompleteAsync(instances, pack, manifest, instance, source, progress, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 半途而废的导入会在列表里留一张点不动的卡 —— 既然这个文件夹是这次造出来的，就一并收掉。
            if (!before.Contains(instance.Id)) instances.Delete(instance.Id);
            throw;
        }
    }

    private async Task<ModpackImportResult> CompleteAsync(
        InstanceManager instances,
        ModpackSource pack,
        ModpackManifest manifest,
        Instance instance,
        IDownloadSource source,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var installer = new VersionInstaller(instances.GameRoot, _http, _settings.DownloadThreads);

        progress?.Report(new InstallProgress($"安装 Minecraft {manifest.MinecraftVersion}", VanillaStart));
        await installer.InstallAsync(manifest.MinecraftVersion, source,
            Window(progress, "安装 Minecraft", VanillaStart, VanillaEnd), ct).ConfigureAwait(false);

        if (manifest.Loader is { } loader && loader.Kind is not ("vanilla" or ""))
        {
            progress?.Report(new InstallProgress($"安装 {LoaderLabel(loader)}", LoaderStart));
            await LoaderInstaller.InstallAsync(installer.Repository, instance.Id, manifest.MinecraftVersion,
                loader, source, _http, ct).ConfigureAwait(false);
        }

        progress?.Report(new InstallProgress("补全实例文件", FilesStart));
        await installer.InstallAsync(instance.Id, manifest.MinecraftVersion, source,
            Window(progress, "补全实例文件", FilesStart, FilesEnd), ct).ConfigureAwait(false);

        var gameDirectory = instances.GameDirectoryFor(instance);
        await DownloadFilesAsync(pack.Archive, manifest, gameDirectory, source, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("铺覆盖层", OverridesStart));
        var written = 0;
        foreach (var folder in manifest.OverrideFolders)
        {
            written += pack.Archive.ExtractFolder(folder, gameDirectory);
        }

        instances.Save();

        progress?.Report(new InstallProgress("导入完成", 1, $"{manifest.Name} · {written} 个覆盖文件"));
        return new ModpackImportResult(instance, null, manifest);
    }

    /// <summary>
    /// 普通压缩包（一整份 .minecraft）不当实例装：铺进 <c>NCL/imported/&lt;包名&gt;</c>，
    /// 让调用方把它登记成一个新的游戏目录 —— 版本、libraries、assets 都在包里了，
    /// 拆成实例只会在启动器自己的树里再抄一份。
    /// </summary>
    private static async Task<string> ExtractAsGameFolder(
        ModpackSource pack, ModpackManifest manifest, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var name = InstanceManager.SanitizeId(string.IsNullOrWhiteSpace(manifest.Name) ? "整合包" : manifest.Name);
        if (name.Length == 0) name = "整合包";

        var parent = Path.Combine(NclPaths.AppDataRoot, "imported");
        NclPaths.EnsureDirectory(parent);

        var target = Path.Combine(parent, name);
        for (var n = 2; Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0; n++)
        {
            target = Path.Combine(parent, $"{name} ({n})");
        }

        progress?.Report(new InstallProgress("解压整合包", 0.1));
        var written = await Task.Run(() => pack.Archive.ExtractEverything(target), ct).ConfigureAwait(false);

        if (written == 0) throw new InvalidDataException("这份压缩包是空的。");

        progress?.Report(new InstallProgress("解压整合包", 0.9, $"{written} 个文件"));
        return target;
    }

    private async Task DownloadFilesAsync(
        ModpackArchive archive,
        ModpackManifest manifest,
        string gameDirectory,
        IDownloadSource source,
        IProgress<InstallProgress>? progress,
        CancellationToken ct)
    {
        var curse = manifest.Files.Any(f => f.ProjectId is not null) ? new CurseForgeFiles(_http) : null;
        var items = new List<DownloadItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < manifest.Files.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var file = manifest.Files[index];

            if (file.ProjectId is not null && file.FileId is not null && curse is not null)
            {
                await curse.PrepareAsync(file.ProjectId, ct).ConfigureAwait(false);
                if (!curse.TryResolve(file.ProjectId, file.FileId, out var url, out var name, out var size))
                {
                    if (file.Required)
                    {
                        throw new IOException($"查不到 CurseForge 组件 {file.ProjectId}/{file.FileId}，这份包可能已经下架了。");
                    }

                    continue;
                }

                // 包里没写 file 路径的条目落进 mods/，文件名要等查到地址才知道 —— 那名字是网上给的，
                // 只取末段，别让一个带 ../ 的文件名把文件写到实例目录外面。
                AddItem(items, seen, gameDirectory,
                    file.RelativePath.Length == 0 ? "mods/" + Path.GetFileName(name) : file.RelativePath,
                    url, size, file.Sha1);
            }
            else
            {
                // 必需文件一个地址都没有 = 包的元数据是坏的。闷头跳过只会装出一个缺 mod 的实例，
                // 启动时才以"某个 mod 没生效"的形式冒出来，那时候谁也想不到是导入漏了一个文件。
                if (file.Urls.Count == 0)
                {
                    if (file.Required)
                    {
                        throw new IOException($"{file.RelativePath} 一个下载地址都没有，这份包的元数据不完整。");
                    }

                    continue;
                }

                AddItem(items, seen, gameDirectory, file.RelativePath, file.Urls[0], file.Size, file.Sha1);
            }

            // 查地址这一段可能要在网上转几圈（CurseForge 按项目逐个查），留一点进度好看它在动。
            progress?.Report(new InstallProgress("准备下载组件",
                ModFilesStart + 0.01 * (index + 1) / manifest.Files.Count, file.RelativePath));
        }

        if (items.Count == 0)
        {
            progress?.Report(new InstallProgress("下载组件", ModFilesEnd));
            return;
        }

        var downloader = new Downloader(_http, _settings.DownloadThreads);
        var report = await downloader.RunAsync("下载组件", items,
            DownloadWindow(progress, "下载组件", ModFilesStart + 0.01, ModFilesEnd), ct).ConfigureAwait(false);

        report.ThrowIfFailed("下载组件");
    }

    private static void AddItem(
        List<DownloadItem> items, HashSet<string> seen, string gameDirectory,
        string relativePath, string url, long size, string? sha1)
    {
        if (!ModpackArchive.IsSafeRelativePath(relativePath)) return;
        if (!seen.Add(relativePath)) return;

        var target = Path.Combine(gameDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        items.Add(new DownloadItem(new[] { url }, target, size > 0 ? size : null, sha1, Path.GetFileName(relativePath))
        {
            CheckExistingHash = sha1 is not null,
        });
    }

    /// <summary>卡片上那颗 tag 的写法：kind + 版本（<c>Fabric 0.16.10</c>），筛选按 kind 分组。</summary>
    private static string LoaderLabel(LoaderRequirement? loader) => loader switch
    {
        null => "原版",
        { Version: { Length: > 0 } version } => $"{DisplayName(loader.Kind)} {version}",
        _ => DisplayName(loader.Kind),
    };

    private static string DisplayName(string kind) => kind switch
    {
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "optifine" => "OptiFine",
        _ => kind,
    };

    private static IProgress<InstallProgress>? Window(
        IProgress<InstallProgress>? progress, string stage, double from, double to)
    {
        if (progress is null) return null;
        var span = Math.Max(0, to - from);
        return new CallbackProgress<InstallProgress>(p =>
            progress.Report(new InstallProgress(stage, from + span * p.Fraction, p.Detail)));
    }

    /// <summary>下载器的进度是"字节数"口径，换算成同一根进度条。</summary>
    private static IProgress<DownloadProgress>? DownloadWindow(
        IProgress<InstallProgress>? progress, string stage, double from, double to)
    {
        if (progress is null) return null;
        var span = Math.Max(0, to - from);
        return new CallbackProgress<DownloadProgress>(d =>
            progress.Report(new InstallProgress(stage, from + span * d.Percent, d.CurrentItem)));
    }
}
