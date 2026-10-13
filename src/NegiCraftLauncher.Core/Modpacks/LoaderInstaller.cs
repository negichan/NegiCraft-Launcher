using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// 把整合包要求的加载器装成一份能用的版本 JSON：写成继承原版的那一个，
/// 剩下的库与资源交给 <see cref="VersionInstaller"/>，它按坐标去重、该下的自己会下。
///
/// <para>Fabric / Quilt 的官方 meta 直接给合并好的 profile，取其中的 mainClass 与 libraries 就够。
/// Forge / NeoForge 没有这种端点，只能把官方安装包（installer jar，本身就是 zip）下载下来，
/// 读它的 <c>install_profile.json</c> —— 里面 <c>versionInfo</c> 就是那份 profile，
/// <c>libraries</c> 是带下载地址的清单，而通用 jar 之类只揣在安装包内部，得搬进 libraries/。</para>
/// </summary>
public static class LoaderInstaller
{
    public static async Task InstallAsync(
        VersionRepository repository,
        string instanceId,
        string minecraftVersion,
        LoaderRequirement loader,
        IDownloadSource source,
        HttpClient http,
        CancellationToken ct)
    {
        switch (loader.Kind)
        {
            case "fabric":
            case "quilt":
                await InstallFabricLike(repository, instanceId, minecraftVersion, loader, source, http, ct)
                    .ConfigureAwait(false);
                break;
            case "forge":
            case "neoforge":
                await InstallForgeLike(repository, instanceId, minecraftVersion, loader, source, http, ct)
                    .ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException(
                    $"{loader.Kind} 加载器还不能自动安装（{minecraftVersion}）。先手动装好这个加载器再导入这份包。");
        }
    }

    private static async Task InstallFabricLike(
        VersionRepository repository, string instanceId, string minecraftVersion,
        LoaderRequirement loader, IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        // Quilt 的 meta 不在 BMCLAPI 的映射表里，Candidates() 会原样退回官方地址。
        var meta = loader.Kind == "quilt" ? "https://meta.quiltmc.org/v3" : "https://meta.fabricmc.net/v2";
        var version = loader.Version ?? await LatestLoaderVersion(meta, minecraftVersion, source, http, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidDataException($"查不到 {minecraftVersion} 的 {loader.Kind} 加载器版本。");
        }

        var json = await HttpText.FetchAsync(http,
            source.Candidates($"{meta}/versions/loader/{minecraftVersion}/{version}/profile/json"), ct).ConfigureAwait(false);

        var merged = VersionProfile.Parse(json);

        // 这份 profile 是"原版 + 加载器"合并后的结果，只挑加载器自己的那几样：
        // 参数留在原版那一份里，两边都写一遍会变成每个参数出现两次。
        repository.Save(instanceId, new VersionProfile
        {
            Id = instanceId,
            InheritsFrom = minecraftVersion,
            MainClass = merged.MainClass,
            Libraries = merged.Libraries,
            JavaVersion = merged.JavaVersion,
        });
    }

    private static async Task<string?> LatestLoaderVersion(
        string meta, string minecraftVersion, IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        var json = await HttpText.FetchAsync(http, source.Candidates($"{meta}/versions/loader/{minecraftVersion}"), ct)
            .ConfigureAwait(false);

        using var document = JsonDocument.Parse(json);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            // 这个端点的第一项就是最新的一版；Fabric 与 Quilt 的形状一致。
            if (entry.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("version", out var value) &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static async Task InstallForgeLike(
        VersionRepository repository, string instanceId, string minecraftVersion,
        LoaderRequirement loader, IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        var version = (loader.Version ?? "").Trim();
        if (version.Length == 0)
        {
            throw new InvalidDataException(
                $"{minecraftVersion} 的这份包没写 {loader.Kind} 版本（CurseForge 的 recommended）。请换一份带明确版本的包。");
        }

        var temp = Path.Combine(NclPaths.CacheDirectory, "modpack-loader", $"{Guid.NewGuid():N}.jar");
        NclPaths.EnsureDirectory(Path.GetDirectoryName(temp)!);

        try
        {
            await FetchInstallerAsync(temp, minecraftVersion, version, loader.Kind, source, http, ct).ConfigureAwait(false);

            using var archive = ZipFile.OpenRead(temp);
            var profile = BuildProfile(archive, instanceId, minecraftVersion, loader.Kind);
            repository.Save(instanceId, profile);
            CopyEmbeddedJars(archive, profile.Libraries, repository.GameRoot);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>依次试可能的下载地址 —— Forge 家族的 artifact id 在不同年代换过名字与位置。</summary>
    private static async Task FetchInstallerAsync(
        string target, string minecraftVersion, string version, string kind,
        IDownloadSource source, HttpClient http, CancellationToken ct)
    {
        var urls = kind == "neoforge"
            ? new[]
            {
                $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar",
                $"https://maven.neoforged.net/releases/net/neoforged/forge/{version}/forge-{version}-installer.jar",
            }
            : new[]
            {
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{minecraftVersion}-{version}/forge-{minecraftVersion}-{version}-installer.jar",
                $"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}/forge-{version}-installer.jar",
            };

        var failures = new List<string>();
        foreach (var candidate in urls.SelectMany(source.Candidates))
        {
            try
            {
                using var response = await http.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    failures.Add($"{Path.GetFileName(candidate)} {(int)response.StatusCode}");
                    continue;
                }

                await using var file = File.Create(target);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(candidate)} {ex.Message}");
            }
        }

        throw new IOException($"下载 {kind} {version} 的安装包失败了（试过：{string.Join("、", failures)}）。");
    }

    private static VersionProfile BuildProfile(ZipArchive archive, string instanceId, string minecraftVersion, string kind)
    {
        var text = ReadEntry(archive, "install_profile.json") ?? ReadEntry(archive, "version.json");
        if (text is null)
        {
            throw new NotSupportedException("这个安装包里没有 install_profile.json / version.json，认不出来。");
        }

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;

        var profile = root.TryGetProperty("versionInfo", out var info) && info.ValueKind == JsonValueKind.Object
            ? info.Deserialize<VersionProfile>(NclJson.Options)!
            : root.Deserialize<VersionProfile>(NclJson.Options)!;

        var mavenLibraries = root.TryGetProperty("libraries", out var libraries) &&
                             libraries.ValueKind == JsonValueKind.Array
            ? libraries.Deserialize<List<LibraryEntry>>(NclJson.Options) ?? new List<LibraryEntry>()
            : new List<LibraryEntry>();

        profile.Id = instanceId;
        if (string.IsNullOrWhiteSpace(profile.InheritsFrom)) profile.InheritsFrom = minecraftVersion;
        if (string.IsNullOrWhiteSpace(profile.MainClass))
        {
            throw new NotSupportedException($"{kind} 的元数据没写 mainClass，装完也起不来。");
        }

        // 带 downloads 的那一份排在前面：LibraryResolver 按坐标去重时留下先出现的那个，
        // 而 versionInfo 里的同名条目常常只给坐标不给地址，让它赢就会缺一整个 jar。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<LibraryEntry>(mavenLibraries.Count + profile.Libraries.Count);
        foreach (var library in mavenLibraries.Concat(profile.Libraries))
        {
            if (seen.Add(library.Name)) merged.Add(library);
        }

        profile.Libraries = merged;
        return profile;
    }

    /// <summary>
    /// 安装包内部那些 <c>…_forge-x.y.z-universal.jar</c> 只存在于包里，元数据只给了坐标。
    /// 按文件名对上号搬进 libraries/，之后 <see cref="LibraryResolver"/> 就当它们本来就在盘上 ——
    /// 这些条目下载不到，缺一个就是启动时报"库文件缺失"。
    /// </summary>
    private static void CopyEmbeddedJars(ZipArchive archive, IReadOnlyList<LibraryEntry> libraries, string gameRoot)
    {
        var librariesRoot = NclPaths.LibrariesDirectory(gameRoot);
        var byFileName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var library in libraries)
        {
            var relative = (library.Downloads?.Artifact?.Path ?? library.Path ??
                            LibraryResolver.BuildRelativePath(library.Name)).Replace('\\', '/');
            byFileName[Path.GetFileName(relative)] = relative;
        }

        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName.Replace('\\', '/'));
            if (name.Length == 0 || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byFileName.TryGetValue(name, out var relative)) continue;

            var target = Path.Combine(librariesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target)) continue;

            NclPaths.EnsureDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 缓存里留一个安装包不影响下次导入（每次都是新的 GUID）。
        }
    }
}
