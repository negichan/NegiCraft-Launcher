using System.IO;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace NegiCraftLauncher.App.Media;

/// <summary>Shares small, static posters without keeping additional video players alive.</summary>
public static class BackgroundThumbnailCache
{
    private const int Edge = 384;
    private const int Capacity = 48;
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, BitmapSource> Cache = new();
    private static readonly LinkedList<string> Recent = new();
    private static readonly Dictionary<string, Task<BitmapSource?>> Pending = new();

    public static Task<BitmapSource?> GetAsync(string path, bool video) => Task.Run(async () =>
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            var key = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{video}";
            Task<BitmapSource?> pending;
            lock (Sync)
            {
                if (Cache.TryGetValue(key, out var image))
                {
                    Recent.Remove(key);
                    Recent.AddFirst(key);
                    return image;
                }
                if (!Pending.TryGetValue(key, out pending!))
                {
                    // Start outside the lock's synchronous continuation path.
                    pending = Task.Run(() => LoadAsync(file.FullName, video));
                    Pending.Add(key, pending);
                }
            }
            BitmapSource? result;
            try { result = await pending.ConfigureAwait(false); }
            catch (Exception) { result = null; }
            lock (Sync)
            {
                if (Pending.TryGetValue(key, out var current) && ReferenceEquals(current, pending))
                    Pending.Remove(key);
                if (result is not null)
                {
                    Cache[key] = result;
                    Recent.Remove(key);
                    Recent.AddFirst(key);
                    while (Recent.Count > Capacity)
                    {
                        Cache.Remove(Recent.Last!.Value);
                        Recent.RemoveLast();
                    }
                }
            }
            return result;
        }
        catch (Exception) { return null; }
    });

    private static async Task<BitmapSource?> LoadAsync(string path, bool video)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem, Edge, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is { Type: ThumbnailType.Image })
            {
                using var stream = thumbnail.AsStreamForRead();
                return Decode(stream);
            }
            if (!video)
            {
                using var stream = File.OpenRead(path);
                return Decode(stream);
            }
            return null;
        }
        finally { Gate.Release(); }
    }

    private static BitmapSource Decode(Stream stream)
    {
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(Edge, frame.PixelWidth);
        else image.DecodePixelHeight = Math.Min(Edge, frame.PixelHeight);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
