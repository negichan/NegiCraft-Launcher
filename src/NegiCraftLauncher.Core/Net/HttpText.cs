using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Net;

namespace NegiCraftLauncher.Core.Net;

/// <summary>
/// Small text fetches with mirror fallback. Separate from <see cref="Downloader"/> because these are
/// parsed in memory rather than written to disk.
/// </summary>
public static class HttpText
{
    public static async Task<string> FetchAsync(HttpClient http, IReadOnlyList<string> urls,
        CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt == 1) await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }
        }

        throw new IOException($"无法获取 {urls[0]}" + (last is null ? "" : $"：{last.Message}"), last);
    }

    public static async Task<byte[]> FetchBytesAsync(HttpClient http, IReadOnlyList<string> urls,
        CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                return await http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw new IOException($"无法获取 {urls[0]}" + (last is null ? "" : $"：{last.Message}"), last);
    }
}
