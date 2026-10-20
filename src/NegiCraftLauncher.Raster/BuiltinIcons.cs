namespace NegiCraftLauncher.Raster;

/// <summary>
/// 随 <c>NegiCraftLauncher.Raster</c> 一起编译进来的<b>加载器标志</b>
/// （Fabric / Forge / Quilt / NeoForge 两家版本）。
/// 五张图都是 <b>80x80</b> —— 图标位 40 DIP，按 2 倍出图，125%~200% 系统缩放下都不糊。
/// 除了 Forge 那张自带深蓝底板，其余是透明底。
///
/// <para><b>为什么只有这四家</b>：Fabric 的 MIT、NeoForge 的 Unlicense 最干净；
/// Forge 用的是它自己打进 jar 的 <c>forge_icon.png</c>（LGPL-2.1 的 Installer 仓库一并覆盖）；
/// Quilt 用 <c>QuiltMC/art</c> 的 mini icon（该仓库未附 LICENSE）。
/// <b>OptiFine 闭源</b>，官方没有任何能当方形标志用的图，所以那一路回落到
/// 「用游戏自己的方块现画」，见 <see cref="PixelArt.CreateBlockCube(PixelBuffer, PixelBuffer, int)"/>。</para>
///
/// <para><b>为什么以 <c>EmbeddedResource</c> 而不是磁盘文件</b>：共享层不能走 Avalonia 的
/// <c>AssetLoader</c>（WPF 侧根本没有），内嵌进程序集是两端唯一都读得到的形态 —— 和
/// <see cref="DefaultSkins"/> 一个路子。</para>
///
/// <para>来源、许可与处理方式见 <c>Assets/ICONS-NOTICE.md</c>。</para>
/// </summary>
public static class BuiltinIcons
{
    private const string Prefix = "NegiCraftLauncher.Raster.Assets.icon_";

    /// <summary>按「名字@尺寸」缓存缩放结果 —— 列表每次刷新都会重算图标，别每次都重新盒式缩一遍。</summary>
    private static readonly Dictionary<string, PixelBuffer> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object Gate = new();

    /// <summary>
    /// 取一张内置标志。<paramref name="size"/> 大于 0 时缩成边长 <paramref name="size"/> 的正方形
    /// （盒式重采样），等于 0 时给原尺寸。资源不存在返回 <c>null</c>，由调用方回退。
    /// </summary>
    public static PixelBuffer? Load(string name, int size = 0)
    {
        var key = size > 0 ? $"{name}@{size}" : name;

        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }

        var native = Read(name);
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
    private static PixelBuffer? Read(string name)
    {
        var resource = $"{Prefix}{name.ToLowerInvariant()}.png";

        try
        {
            using var stream = typeof(BuiltinIcons).Assembly.GetManifestResourceStream(resource);
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
