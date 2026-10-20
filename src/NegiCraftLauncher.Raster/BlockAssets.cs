namespace NegiCraftLauncher.Raster;

/// <summary>
/// 随 <c>NegiCraftLauncher.Raster</c> 一起编译进来的<b>等距方块图标</b>
/// （草方块 / 石头 / TNT / 铁砧 …），原版实例按 id 散列挑一个、加载器指定一个、
/// 用户在「实例图标」里自己挑一个，都走这里。
///
/// <para><b>来源</b>：ccVaults（ccLeaf 旗下）的预渲染等距方块图，<b>1024x1024 原图原样入库</b>，
/// 不做任何预缩。18 张合计约 <b>339 KiB</b>。</para>
///
/// <para><b>为什么不预缩</b>：这批图的底子是 64x64 渲染、最近邻放大 16 倍，有效分辨率上限
/// 就是 64x64 —— 缩到 128 不会多出信息，反而让上屏多经过一次重采样。直接给原图、由显示端
/// 一次缩到设备像素，是这条链路上唯一不额外插值的选择。解码慢一点（单张约 25 ms，且有缓存），
/// 换来的是「看到的确实是原图」。</para>
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
    /// 等于 0（默认）时给原尺寸 1024。没有这张图返回 <c>null</c>，由调用方回退。
    /// </summary>
    public static PixelBuffer? Load(string name, int size = 0) => EmbeddedImages.Load(Prefix, name, size);
}
