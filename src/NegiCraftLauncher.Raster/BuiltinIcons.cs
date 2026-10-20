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
/// <para><b>OptiFine 闭源</b>，官方没有任何能当方形标志用的图，同样回落到「用游戏自己的方块」，
/// 见 <see cref="BlockAssets"/>。</para>
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

    /// <summary>
    /// 取一张内置标志。<paramref name="size"/> 大于 0 时缩成边长 <paramref name="size"/> 的正方形
    /// （盒式重采样），等于 0 时给原尺寸。资源不存在返回 <c>null</c>，由调用方回退。
    /// </summary>
    public static PixelBuffer? Load(string name, int size = 0) => EmbeddedImages.Load(Prefix, name, size);
}
