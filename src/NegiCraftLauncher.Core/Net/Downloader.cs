using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace NegiCraftLauncher.Core.Net;

public sealed record DownloadItem(
    IReadOnlyList<string> Urls,
    string TargetPath,
    long? Size = null,
    string? Sha1 = null,
    string? Label = null)
{
    /// <summary>Re-hash files that already exist. Worth it for the few large, critical files; too
    /// slow to do for the thousands of small asset objects, which are verified once on arrival.</summary>
    public bool CheckExistingHash { get; init; }

    public static DownloadItem Of(string url, string targetPath, long? size = null, string? sha1 = null,
        string? label = null, bool checkExistingHash = false) =>
        new(new[] { url }, targetPath, size, sha1, label) { CheckExistingHash = checkExistingHash };
}

public sealed record DownloadFailure(string TargetPath, string Url, string Reason);

public sealed class DownloadReport
{
    public required int Total { get; init; }
    public int Fetched { get; init; }
    public int Reused { get; init; }
    public long BytesTransferred { get; init; }
    public IReadOnlyList<DownloadFailure> Failures { get; init; } = [];

    public bool Success => Failures.Count == 0;

    public void ThrowIfFailed(string stage)
    {
        if (Success) return;
        var first = Failures[0];
        var detail = Failures.Count == 1
            ? first.Reason
            : $"{first.Reason}（另有 {Failures.Count - 1} 个文件失败）";
        throw new IOException($"{stage}失败：{Path.GetFileName(first.TargetPath)} — {detail}");
    }
}

public sealed record DownloadProgress(
    string Stage,
    int TotalItems,
    int CompletedItems,
    long TotalBytes,
    long CompletedBytes,
    string? CurrentItem)
{
    public double Percent => TotalBytes > 0
        ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1)
        : (TotalItems == 0 ? 1 : Math.Clamp((double)CompletedItems / TotalItems, 0, 1));
}

public sealed class Downloader
{
    private const string TempSuffix = ".nclpart";
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    // Below this a file is quicker single-streamed than split; above it the fetch is cut into
    // ranged segments so one big jar can hold several connections at once.
    private const long SegmentSize = 8L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly int _maxConcurrency;
    private readonly int _maxAttemptsPerUrl;

    // Caps open connections, not files: a segmented fetch takes one slot per segment, so the
    // thread count the user picks is the real number of sockets in flight.
    private readonly SemaphoreSlim _slots;

    public Downloader(HttpClient? http = null, int maxConcurrency = 16, int maxAttemptsPerUrl = 3)
    {
        _http = http ?? NclHttp.Shared;
        _maxConcurrency = Math.Max(1, maxConcurrency);
        _maxAttemptsPerUrl = Math.Max(1, maxAttemptsPerUrl);
        _slots = new SemaphoreSlim(_maxConcurrency);
    }

    public async Task<DownloadReport> RunAsync(
        string stage,
        IReadOnlyList<DownloadItem> items,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            progress?.Report(new DownloadProgress(stage, 0, 0, 0, 0, null));
            return new DownloadReport { Total = 0 };
        }

        var totalBytes = items.Sum(i => i.Size ?? 0);
        long completedBytes = 0;
        var completedItems = 0;
        var fetched = 0;
        var reused = 0;
        var failures = new List<DownloadFailure>();
        var failuresLock = new object();
        var lastPublish = Stopwatch.GetTimestamp();
        var currentLabel = "";

        void Publish(bool force = false)
        {
            if (!force)
            {
                // Asset batches can be thousands of files; flooding the UI thread with one report
                // per file makes the progress bar stutter instead of animate.
                if (Stopwatch.GetElapsedTime(lastPublish).TotalMilliseconds < 80) return;
            }

            lastPublish = Stopwatch.GetTimestamp();
            progress?.Report(new DownloadProgress(stage, items.Count,
                Volatile.Read(ref completedItems), totalBytes, Interlocked.Read(ref completedBytes), currentLabel));
        }

        // Byte-level ticks, so a single big file moves the bar instead of jumping 0→100.
        void AddBytes(long n)
        {
            Interlocked.Add(ref completedBytes, n);
            Publish();
        }

        Publish(force: true);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = _maxConcurrency,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), options, async (index, token) =>
        {
            var item = items[index];
            NclPaths.EnsureDirectory(Path.GetDirectoryName(item.TargetPath)!);
            currentLabel = item.Label ?? Path.GetFileName(item.TargetPath);

            if (File.Exists(item.TargetPath) && ExistingFileIsUsable(item))
            {
                Interlocked.Increment(ref reused);
                Interlocked.Add(ref completedBytes, item.Size ?? new FileInfo(item.TargetPath).Length);
                Interlocked.Increment(ref completedItems);
                Publish();
                return;
            }

            // Live ticks already feed completedBytes, so the settlement below only tops up what
            // the ticks never saw; adding the whole size again would double-count every file.
            long ticked = 0;
            void OnBytes(long n)
            {
                Interlocked.Add(ref ticked, n);
                AddBytes(n);
            }

            DownloadFailure? failure = null;
            foreach (var url in item.Urls)
            {
                for (var attempt = 1; attempt <= _maxAttemptsPerUrl; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        await FetchAsync(url, item, OnBytes, token).ConfigureAwait(false);
                        failure = null;
                        break;
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failure = new DownloadFailure(item.TargetPath, url, Describe(ex));
                        if (attempt < _maxAttemptsPerUrl)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), token).ConfigureAwait(false);
                        }
                    }
                }

                if (failure is null) break;
            }

            if (failure is null)
            {
                Interlocked.Increment(ref fetched);
            }
            else
            {
                lock (failuresLock) failures.Add(failure);
                var unsettled = (item.Size ?? 0) - Interlocked.Read(ref ticked);
                if (unsettled > 0) Interlocked.Add(ref completedBytes, unsettled);
            }

            Interlocked.Increment(ref completedItems);
            Publish();
        }).ConfigureAwait(false);

        Publish(force: true);

        return new DownloadReport
        {
            Total = items.Count,
            Fetched = Volatile.Read(ref fetched),
            Reused = Volatile.Read(ref reused),
            BytesTransferred = Interlocked.Read(ref completedBytes),
            Failures = failures,
        };
    }

    private bool ExistingFileIsUsable(DownloadItem item)
    {
        try
        {
            var info = new FileInfo(item.TargetPath);
            if (info.Length == 0) return false;
            if (item.Size is { } expected && info.Length != expected) return false;
            if (item.CheckExistingHash && item.Sha1 is { } sha1)
            {
                return string.Equals(HashFile(item.TargetPath), sha1, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task FetchAsync(string url, DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        var tempPath = item.TargetPath + TempSuffix;

        try
        {
            var segmented = item.Size is >= SegmentSize * 2 && _maxConcurrency >= 2
                            && await FetchSegmentedAsync(url, tempPath, item.Size.Value, onBytes, ct).ConfigureAwait(false);
            if (!segmented)
            {
                await FetchWholeAsync(url, tempPath, onBytes, ct).ConfigureAwait(false);
            }

            VerifyAndCommit(tempPath, item);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>
    ///     Splits one file into ranged segments downloaded in parallel. Returns false when the
    ///     server answers the first range request with 200 instead of 206, in which case the
    ///     caller falls back to a plain single-stream fetch.
    /// </summary>
    private async Task<bool> FetchSegmentedAsync(string url, string tempPath, long size,
        Action<long> onBytes, CancellationToken ct)
    {
        var segments = (int)Math.Clamp(size / SegmentSize, 2, Math.Min(4, _maxConcurrency));
        var chunk = (size + segments - 1) / segments;

        // The first segment doubles as the range-support probe.
        using var probe = await SendRangeAsync(url, 0, chunk - 1, ct).ConfigureAwait(false);
        if (probe.StatusCode != HttpStatusCode.PartialContent) return false;

        await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        target.SetLength(size);
        var handle = target.SafeFileHandle;

        var workers = new List<Task>(segments) { CopySegmentAsync(probe, handle, 0, onBytes, ct) };
        for (var start = chunk; start < size; start += chunk)
        {
            var from = start;
            var to = Math.Min(size, start + chunk) - 1;
            workers.Add(Task.Run(async () =>
            {
                using var response = await SendRangeAsync(url, from, to, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await CopySegmentAsync(response, handle, from, onBytes, ct).ConfigureAwait(false);
            }, ct));
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return true;
    }

    private Task<HttpResponseMessage> SendRangeAsync(string url, long from, long to, CancellationToken ct) =>
        _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url)
        {
            Headers = { Range = new RangeHeaderValue(from, to) },
        }, HttpCompletionOption.ResponseHeadersRead, ct);

    private async Task CopySegmentAsync(HttpResponseMessage response, SafeFileHandle handle, long offset,
        Action<long> onBytes, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var slot = await AcquireSlotAsync(ct).ConfigureAwait(false);

        var position = offset;
        await CopyWithStallTimeoutAsync(source, async (chunk, token) =>
        {
            // pread/pwrite semantics: concurrent segments write disjoint offsets of one handle.
            await RandomAccess.WriteAsync(handle, chunk, position, token).ConfigureAwait(false);
            position += chunk.Length;
        }, onBytes, ct).ConfigureAwait(false);
    }

    private async Task FetchWholeAsync(string url, string tempPath, Action<long> onBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var slot = await AcquireSlotAsync(ct).ConfigureAwait(false);

        await CopyWithStallTimeoutAsync(source, (chunk, token) => target.WriteAsync(chunk, token), onBytes, ct)
            .ConfigureAwait(false);
    }

    private static void VerifyAndCommit(string tempPath, DownloadItem item)
    {
        var written = new FileInfo(tempPath).Length;
        if (item.Size is { } expected && written != expected)
        {
            throw new IOException($"大小不符（期望 {expected}，实际 {written}）");
        }

        if (item.Sha1 is { } sha1)
        {
            var actual = HashFile(tempPath);
            if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"校验和不符（期望 {sha1[..Math.Min(12, sha1.Length)]}…，实际 {actual[..Math.Min(12, actual.Length)]}…）");
            }
        }

        File.Move(tempPath, item.TargetPath, overwrite: true);
    }

    private async ValueTask<IDisposable> AcquireSlotAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Slot(_slots);
    }

    private sealed class Slot : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public Slot(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => _semaphore.Release();
    }

    private static async Task CopyWithStallTimeoutAsync(Stream source,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write, Action<long> onBytes, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        long lastActivity = Stopwatch.GetTimestamp();

        // A connection that accepts but never answers would otherwise block the slot forever.
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await Task.Delay(2000, linked.Token).ConfigureAwait(false);
                    if (Stopwatch.GetElapsedTime(Interlocked.Read(ref lastActivity)) > StallTimeout)
                    {
                        linked.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read == 0) break;
                await write(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
                onBytes(read);
                Interlocked.Exchange(ref lastActivity, Stopwatch.GetTimestamp());
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"连接停滞超过 {(int)StallTimeout.TotalSeconds} 秒");
        }
        finally
        {
            // The watchdog only stops itself when it decides a download has stalled, so a completed
            // copy has to release it; otherwise every file waits out the whole stall timeout.
            linked.Cancel();
            await watchdog.ConfigureAwait(false);
        }
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http when http.StatusCode is { } code => $"HTTP {(int)code} {code}",
        HttpRequestException http => http.Message,
        IOException io => io.Message,
        _ => ex.Message,
    };
}
