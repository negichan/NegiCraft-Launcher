using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Modrinth;

/// <summary>Where each resource kind belongs inside a game directory.</summary>
public static class ResourceFolders
{
    public static string For(string category) => category switch
    {
        "mod" => "mods",
        "rp" => "resourcepacks",
        "shader" => "shaderpacks",
        "datapack" => "datapacks",
        _ => throw new ArgumentException($"未知的资源类别 {category}", nameof(category)),
    };
}

/// <summary>Downloads one published file into an instance's game directory.</summary>
public sealed class ModrinthInstaller
{
    private readonly Downloader _downloader;

    public ModrinthInstaller(HttpClient? http = null, int concurrency = 4) =>
        _downloader = new Downloader(http ?? NclHttp.Shared, concurrency);

    public async Task<string> InstallAsync(
        ModrinthVersion version,
        string gameDirectory,
        string category,
        IDownloadSource source,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var directory = Path.Combine(gameDirectory, ResourceFolders.For(category));
        NclPaths.EnsureDirectory(directory);

        var target = Path.Combine(directory, version.SafeFileName);
        if (File.Exists(target)) return target;

        var report = await _downloader.RunAsync("下载资源", new[]
        {
            new DownloadItem(source.Candidates(version.Url), target,
                version.Size > 0 ? version.Size : null, version.Sha1, version.SafeFileName),
        }, progress, ct).ConfigureAwait(false);

        report.ThrowIfFailed("下载资源");
        return target;
    }
}
