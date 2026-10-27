using System.IO;
using System.Security.Cryptography;
using System.Text;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using NegiCraftLauncher.Core;

namespace NegiCraftLauncher.App.Media;

/// <summary>Builds bounded playback copies with the Windows codecs; originals are never modified.</summary>
public sealed class BackgroundVideoCache
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly string _directory;

    public BackgroundVideoCache(string? directory = null) =>
        _directory = directory ?? Path.Combine(NclPaths.CacheDirectory, "background-video");

    public string CachePath(string source, string quality)
    {
        var file = new FileInfo(source);
        var identity = $"{file.Length}:{file.LastWriteTimeUtc.Ticks}:{quality}:v1";
        return Path.Combine(_directory, $"{SourceKey(source)}-{Hash(identity)}.mp4");
    }

    public Task<string> PrepareAsync(string source, string quality, IProgress<double>? progress,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (quality == "original") return source;

        var destination = CachePath(source, quality);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
                return destination;
            }

            Directory.CreateDirectory(_directory);
            var input = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(source)).AsTask(cancellationToken);
            var profile = await MediaEncodingProfile.CreateFromFileAsync(input).AsTask(cancellationToken);
            if (profile.Video is not { Width: > 0, Height: > 0 } video)
                throw new InvalidDataException("文件中没有可用的视频画面。");

            var limit = quality == "clear" ? 1920.0 : 1280.0;
            var scale = Math.Min(1, limit / Math.Max(video.Width, video.Height));
            var width = Math.Max(2, (uint)(video.Width * scale) / 2 * 2);
            var height = Math.Max(2, (uint)(video.Height * scale) / 2 * 2);
            var outputProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            outputProfile.Video.Width = width;
            outputProfile.Video.Height = height;
            outputProfile.Video.Bitrate = quality == "clear" ? 5_000_000u : 2_500_000u;
            // Keep slower sources at their original rate, and bound high-frame-rate wallpapers.
            var fps = video.FrameRate.Denominator == 0 ? 30 :
                video.FrameRate.Numerator / (double)video.FrameRate.Denominator;
            outputProfile.Video.FrameRate.Numerator = fps > 0 && fps <= 30 ? video.FrameRate.Numerator : 30;
            outputProfile.Video.FrameRate.Denominator = fps > 0 && fps <= 30 ? video.FrameRate.Denominator : 1;
            if (profile.Audio is null) outputProfile.Audio = null;

            temporary = Path.Combine(_directory, $"{Guid.NewGuid():N}.partial.mp4");
            var output = await StorageFile.GetFileFromPathAsync(
                CreateEmptyFile(temporary)).AsTask(cancellationToken);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, outputProfile)
                .AsTask(cancellationToken);
            if (!prepared.CanTranscode)
                throw new InvalidOperationException($"Windows 无法优化这段视频（{prepared.FailureReason}）。");

            await prepared.TranscodeAsync().AsTask(cancellationToken, progress);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            Trim(destination);
            return destination;
        }
        finally
        {
            if (temporary is not null)
            {
                // Cancellation may finish before the Windows encoder releases its output handle.
                for (var attempt = 0; attempt < 20 && File.Exists(temporary); attempt++)
                {
                    TryDelete(temporary);
                    if (File.Exists(temporary)) await Task.Delay(100).ConfigureAwait(false);
                }
            }
            Gate.Release();
        }
    }, cancellationToken);

    public async Task RemoveAsync(string source)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_directory)) return;
            foreach (var path in Directory.EnumerateFiles(_directory, $"{SourceKey(source)}-*.mp4"))
                TryDelete(path);
        }
        finally { Gate.Release(); }
    }

    private static string CreateEmptyFile(string path)
    {
        using (File.Create(path)) { }
        return path;
    }

    private void Trim(string keep)
    {
        const long budget = 256L * 1024 * 1024;
        var files = new DirectoryInfo(_directory).GetFiles("*.mp4")
            .Where(f => !f.Name.EndsWith(".partial.mp4", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        long bytes = 0;
        foreach (var file in files)
        {
            bytes += file.Length;
            if (bytes > budget && !file.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase))
                TryDelete(file.FullName);
        }
    }

    private static string SourceKey(string source) => Hash(Path.GetFullPath(source).ToUpperInvariant());
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
