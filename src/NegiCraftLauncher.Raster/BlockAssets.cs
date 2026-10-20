namespace NegiCraftLauncher.Raster;

/// <summary>
/// 随 <c>NegiCraftLauncher.Raster</c> 一起编译进来的<b>等距方块图标</b>
/// （草方块 / 石头 / TNT / 铁砧 …），原版实例按 id 散列挑一个、加载器指定一个，都走这里。
///
/// <para><b>来源</b>：ccVaults（ccLeaf 旗下）的预渲染等距方块图，原图 1024x1024，
/// 盒式缩到 <b>128x128</b>。为什么是 128 而不是 80：图标位 40 DIP，系统缩放 250% 时
/// 要 100 个物理像素，80 就得被放大、方块棱线发糊；128 在任何常见缩放下都是缩小。
/// 18 张加起来约 146 KiB。</para>
///
/// <para><b>为什么是整图而不是现读 jar 贴图再拼</b>：铁砧这类非完整方块按模型几何拼
/// 既要维护四段坐标、又对投影偏移极其敏感（差半个画布就整体畸形）；ccVaults 这批图
/// 造型和取景都是准的，直接拿来当图标更稳。代价是图标不再跟着玩家的资源包变。</para>
///
/// <para>授权状况（<b>All Rights Reserved</b>，非自由许可）与处理参数见
/// <c>Assets/ICONS-NOTICE.md</c>。</para>
/// </summary>
public static class BlockAssets
{
    private const string Prefix = "NegiCraftLauncher.Raster.Assets.block_";

    /// <summary>
    /// 取一张方块图标。<paramref name="size"/> 大于 0 时缩成边长 <paramref name="size"/> 的正方形，
    /// 等于 0 时给原尺寸（128）。没有这张图返回 <c>null</c>，由调用方回退。
    /// </summary>
    public static PixelBuffer? Load(string name, int size = 0) => EmbeddedImages.Load(Prefix, name, size);
}
