namespace NegiCraftLauncher.Raster;

/// <summary>
/// 随 <c>NegiCraftLauncher.Raster</c> 一起编译进来的<b>加载器标志</b>（Fabric / Quilt / NeoForge）。
/// 前三张都是 <b>80x80</b> —— 图标位 40 DIP，按 2 倍出图，125%~200% 系统缩放下都不糊，全是透明底。
///
/// <para><b>为什么没有 Forge</b>：Forge 的标志只在它 Installer 仓库里，形态是带深蓝底板的横幅
/// （裁成方形观感和旁边那圈方块对不上），jar 里那张 <c>forge_icon.png</c> 也一样。<b>改用游戏里的
/// 铁砧方块</b> —— 和原版实例那批立方体图标一套观感，且零授权负担。见
/// <c>MainWindowViewModel.ForgeBlockType</c>。</para>
///
/// <para><b>OptiFine 闭源</b>，官方没有任何能当方形标志用的图，同样回落到「用游戏自己的方块现画」，
/// 见 <see cref="PixelArt.CreateBlockCube(PixelBuffer, PixelBuffer, int)"/>。</para>
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
