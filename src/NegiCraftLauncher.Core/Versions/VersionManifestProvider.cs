using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Versions;

/// <summary>
/// Serves the version list, preferring a live fetch and falling back to the on-disk cache when the
/// network is unavailable so an offline session can still list and launch what is installed.
/// </summary>
public sealed class VersionManifestProvider
{
    private readonly HttpClient _http;
    private VersionManifest? _memory;

    public VersionManifestProvider(HttpClient? http = null) => _http = http ?? NclHttp.Shared;

    public async Task<VersionManifest> GetAsync(IDownloadSource source, bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (_memory is not null && !forceRefresh) return _memory;

        if (!forceRefresh)
        {
            var cached = TryReadCache();
            if (cached is not null) return _memory = cached;
        }

        try
        {
            // Start from the official URL and let the source map it, so a mirror that is unreachable
            // still falls back to Mojang. Feeding the source's own URL would produce a single candidate.
            var official = OfficialDownloadSource.Instance.VersionManifestUrl;
            var json = await HttpText.FetchAsync(_http, source.Candidates(official), ct).ConfigureAwait(false);
            var manifest = VersionSummary.Parse(json);
            WriteCache(json);
            return _memory = manifest;
        }
        catch (Exception) when (!forceRefresh)
        {
            var cached = TryReadCache();
            if (cached is not null) return _memory = cached;
            throw;
        }
    }

    private static VersionManifest? TryReadCache()
    {
        try
        {
            return File.Exists(NclPaths.VersionManifestCache)
                ? VersionSummary.Parse(File.ReadAllText(NclPaths.VersionManifestCache))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteCache(string json)
    {
        try
        {
            NclPaths.EnsureDirectory(NclPaths.CacheDirectory);
            File.WriteAllText(NclPaths.VersionManifestCache, json);
        }
        catch (Exception)
        {
            // A read-only profile or a full disk should not stop the launcher from working this session.
        }
    }
}
