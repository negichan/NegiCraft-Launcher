using System.Reflection;

namespace NegiCraftLauncher.Raster;

/// <summary>
/// 内置皮肤（Steve / Alex / Miku_Mew）。共享层不能走 Avalonia 的 <c>AssetLoader</c>，
/// 所以这些 PNG 以 <c>EmbeddedResource</c> 形式随 <c>NegiCraftLauncher.Raster</c> 一起编译进来。
/// </summary>
public static class DefaultSkins
{
    private const string Prefix = "NegiCraftLauncher.Raster.Assets.";

    /// <summary>取一张内置皮肤，<paramref name="stem"/> 是资源名去掉 <c>skin_</c> 前缀与扩展名的部分。</summary>
    public static byte[]? TryRead(string stem)
    {
        var name = $"{Prefix}skin_{stem.ToLowerInvariant()}.png";
        using var stream = typeof(DefaultSkins).Assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static byte[] Bytes(bool slim) => TryRead(slim ? "alex" : "steve")!;

    public static uint[] Pixels(bool slim) =>
        SkinTexture.Decode(Bytes(slim))?.ToRenderPixels() ?? Fallback;

    /// <summary>解码都失败时的兜底：一整块肤色。</summary>
    public static uint[] Fallback
    {
        get
        {
            var pixels = new uint[64 * 64];
            Array.Fill(pixels, 0xFFC49A76);
            return pixels;
        }
    }
}
