using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Versions;

public sealed record InstallProgress(string Stage, double Fraction, string? Detail = null);

/// <summary>
/// Invokes its callback on whatever thread reports it; callers that touch UI must marshal themselves.
/// Avoids <see cref="Progress{T}"/>'s captured-context behaviour, which silently drops reports made
/// from a continuation.
/// </summary>
public sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;

    public CallbackProgress(Action<T> handler) => _handler = handler;

    public void Report(T value) => _handler(value);
}

/// <summary>
/// Makes a version fully present on disk: profile JSON, client jar, libraries, asset index and every
/// asset object. Everything is content-addressed and skipped when already complete, so re-running is
/// cheap and resumable.
/// </summary>
public sealed class VersionInstaller
{
    private const double MetadataWeight = 0.02;
    private const double JarWeight = 0.10;
    private const double LibraryWeight = 0.18;
    private const double AssetWeight = 0.68;
    private const double LoggingWeight = 0.02;

    private readonly string _gameRoot;
    private readonly HttpClient _http;
    private readonly Downloader _downloader;

    public VersionRepository Repository { get; }
    public VersionManifestProvider Manifests { get; }

    public VersionInstaller(string gameRoot, HttpClient? http = null, int concurrency = 16)
    {
        _gameRoot = gameRoot;
        _http = http ?? NclHttp.Shared;
        _downloader = new Downloader(_http, concurrency);
        Repository = new VersionRepository(gameRoot);
        Manifests = new VersionManifestProvider(_http);
    }

    /// <summary>Downloads a version's metadata and every file it needs, then returns the resolved profile.</summary>
    public async Task<ResolvedVersion> InstallAsync(
        string versionId,
        IDownloadSource source,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var jarStart = MetadataWeight;
        var libraryStart = jarStart + JarWeight;
        var assetStart = libraryStart + LibraryWeight;
        var loggingStart = assetStart + AssetWeight;

        progress?.Report(new InstallProgress("获取版本信息", 0));

        var manifest = await Manifests.GetAsync(source, ct: ct).ConfigureAwait(false);
        var summary = manifest.Find(versionId)
            ?? throw new ArgumentException($"版本清单中不存在 {versionId}", nameof(versionId));

        await EnsureProfileAsync(versionId, summary, source, ct).ConfigureAwait(false);
        progress?.Report(new InstallProgress("解析版本信息", MetadataWeight));

        var resolved = Repository.Load(versionId);
        if (resolved.Profile.AssetIndex is null || string.IsNullOrEmpty(resolved.Profile.AssetIndex.Url))
        {
            throw new NotSupportedException($"{versionId} 没有资源索引，1.7.2 以前的版本暂不支持");
        }

        await EnsureClientJarAsync(resolved, source, jarStart, JarWeight, progress, ct).ConfigureAwait(false);
        await EnsureLibrariesAsync(resolved, source, libraryStart, LibraryWeight, progress, ct).ConfigureAwait(false);
        await EnsureAssetsAsync(resolved, source, assetStart, AssetWeight, progress, ct).ConfigureAwait(false);
        await EnsureLoggingAsync(resolved, source, loggingStart, LoggingWeight, progress, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("完成", 1));
        return resolved;
    }

    private async Task EnsureProfileAsync(string versionId, VersionSummary summary, IDownloadSource source,
        CancellationToken ct)
    {
        if (Repository.ProfileExists(versionId)) return;

        var bytes = await HttpText.FetchBytesAsync(_http, source.Candidates(summary.Url), ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(summary.Sha1))
        {
            var actual = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actual, summary.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"版本 {versionId} 的元数据校验失败，请更换下载源后重试");
            }
        }

        Repository.SaveRawJson(versionId, Encoding.UTF8.GetString(bytes));
    }

    private async Task EnsureClientJarAsync(ResolvedVersion resolved, IDownloadSource source,
        double from, double weight, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var client = resolved.Profile.Downloads?.Client;
        if (client is null || string.IsNullOrEmpty(client.Url))
        {
            throw new InvalidOperationException($"版本 {resolved.InstanceId} 的元数据没有提供客户端下载地址");
        }

        var target = resolved.ClientJarPath;
        progress?.Report(new InstallProgress("下载游戏核心", from));

        if (File.Exists(target) && new FileInfo(target).Length == client.Size && client.Size > 0)
        {
            progress?.Report(new InstallProgress("下载游戏核心", from + weight));
            return;
        }

        var report = await _downloader.RunAsync("下载游戏核心", new[]
        {
            new DownloadItem(source.Candidates(client.Url), target, client.Size, client.Sha1, "client.jar")
            {
                CheckExistingHash = true,
            },
        }, Window(progress, "下载游戏核心", from, from + weight), ct).ConfigureAwait(false);

        report.ThrowIfFailed("下载游戏核心");
    }

    private async Task EnsureLibrariesAsync(ResolvedVersion resolved, IDownloadSource source,
        double from, double weight, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var resolution = LibraryResolver.Resolve(
            resolved.Profile.Libraries, resolved.LibrariesRoot, source);

        var items = resolution.All
            .Where(l => l.NeedsDownload)
            .Select(l => new DownloadItem(l.Urls, l.LocalPath, l.Size > 0 ? l.Size : null, l.Sha1,
                Path.GetFileName(l.RelativePath))
            {
                CheckExistingHash = true,
            })
            .ToList();

        progress?.Report(new InstallProgress("补全库文件", from));
        if (items.Count == 0)
        {
            progress?.Report(new InstallProgress("补全库文件", from + weight));
            return;
        }

        var report = await _downloader.RunAsync("补全库文件", items,
            Window(progress, "补全库文件", from, from + weight), ct).ConfigureAwait(false);
        report.ThrowIfFailed("补全库文件");
    }

    private async Task EnsureAssetsAsync(ResolvedVersion resolved, IDownloadSource source,
        double from, double weight, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var indexEntry = resolved.Profile.AssetIndex!;
        var indexPath = resolved.AssetIndexPath;

        progress?.Report(new InstallProgress("获取资源索引", from));

        if (!File.Exists(indexPath))
        {
            NclPaths.EnsureDirectory(Path.GetDirectoryName(indexPath)!);
            var json = await HttpText.FetchAsync(_http, source.Candidates(indexEntry.Url), ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(indexEntry.Sha1))
            {
                var actual = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
                if (!string.Equals(actual, indexEntry.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("资源索引校验失败，请更换下载源后重试");
                }
            }

            File.WriteAllText(indexPath, json);
        }

        var index = AssetIndex.Parse(File.ReadAllText(indexPath));
        var objectsRoot = NclPaths.AssetObjectsDirectory(_gameRoot);

        var items = new List<DownloadItem>(index.Objects.Count);
        foreach (var (name, asset) in index.Objects)
        {
            if (string.IsNullOrEmpty(asset.Hash)) continue;
            var relative = asset.RelativePath;
            var url = $"https://resources.download.minecraft.net/{relative}";
            items.Add(new DownloadItem(source.Candidates(url), Path.Combine(objectsRoot, relative),
                asset.Size, asset.Hash, name));
        }

        progress?.Report(new InstallProgress("补全资源文件", from));
        if (items.Count == 0)
        {
            progress?.Report(new InstallProgress("补全资源文件", from + weight));
            return;
        }

        var report = await _downloader.RunAsync("补全资源文件", items,
            Window(progress, "补全资源文件", from, from + weight), ct).ConfigureAwait(false);
        report.ThrowIfFailed("补全资源文件");
    }

    private async Task EnsureLoggingAsync(ResolvedVersion resolved, IDownloadSource source,
        double from, double weight, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new InstallProgress("准备日志配置", from));

        if (resolved.Profile.Logging is not { } logging ||
            !logging.TryGetValue("client", out var entry) ||
            string.IsNullOrEmpty(entry.File.Url))
        {
            progress?.Report(new InstallProgress("准备日志配置", from + weight));
            return;
        }

        // The vanilla layout keeps logging configs alongside the assets, not under versions/.
        var target = Path.Combine(resolved.AssetsRoot, entry.File.Id);
        var report = await _downloader.RunAsync("准备日志配置", new[]
        {
            new DownloadItem(source.Candidates(entry.File.Url), target, entry.File.Size, entry.File.Sha1,
                entry.File.Id),
        }, Window(progress, "准备日志配置", from, from + weight), ct).ConfigureAwait(false);

        report.ThrowIfFailed("准备日志配置");
    }

    private static IProgress<DownloadProgress>? Window(
        IProgress<InstallProgress>? progress, string stage, double from, double to)
    {
        if (progress is null) return null;
        var span = Math.Max(0, to - from);
        return new CallbackProgress<DownloadProgress>(d =>
            progress.Report(new InstallProgress(stage, from + span * d.Percent, d.CurrentItem)));
    }
}
