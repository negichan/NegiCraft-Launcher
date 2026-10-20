namespace NegiCraftLauncher.Raster;

/// <summary>
/// 读<b>内嵌进程序集的 PNG</b> 并缓存 —— <see cref="BuiltinIcons"/>（加载器标志）与
/// <see cref="BlockAssets"/>（等距方块图标）共用这一套，免得两份解码 + 两份缓存慢慢走偏。
///
/// <para>调用方只负责给前缀：标志是 <c>Assets.icon_*</c>，方块是 <c>Assets.block_*</c>。
/// 名字大小写不敏感，和资源名的实际大小写无关（.NET 的清单资源查找本来就是大小写敏感的，
/// 所以这里统一转小写再拼）。</para>
/// </summary>
public static class EmbeddedImages
{
    /// <summary>按「前缀@名字@尺寸」缓存 —— 列表每次刷新都会重算图标，别每次都重新解码。</summary>
    private static readonly Dictionary<string, PixelBuffer> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// 取一张内嵌图。<paramref name="size"/> 大于 0 时缩成边长 <paramref name="size"/> 的正方形
    /// （盒式重采样），等于 0 时给原尺寸。资源不存在返回 <c>null</c>，由调用方回退。
    /// </summary>
    public static PixelBuffer? Load(string prefix, string name, int size = 0)
    {
        var key = size > 0 ? $"{prefix}{name}@{size}" : $"{prefix}{name}";

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var native = Read(prefix, name);
        if (native is null) return null;

        // Downsample 只在长边超过目标时才动手，且保持长宽比；图标都是正方形，出来就是 size x size。
        var result = size > 0 ? PixelBuffer.Downsample(native, size) : native;

        lock (Gate)
        {
            Cache[key] = result;
        }

        return result;
    }

    /// <summary>解码后的原尺寸图（不缓存，<see cref="Load"/> 负责缓存）。</summary>
    private static PixelBuffer? Read(string prefix, string name)
    {
        var resource = $"{prefix}{name.ToLowerInvariant()}.png";

        try
        {
            using var stream = typeof(EmbeddedImages).Assembly.GetManifestResourceStream(resource);
            if (stream is null) return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return PngCodec.Decode(buffer.ToArray()).ToPixelBuffer();
        }
        catch
        {
            // 资源缺失或解码失败：当作没有这张图，调用方自己回退。
            return null;
        }
    }
}
