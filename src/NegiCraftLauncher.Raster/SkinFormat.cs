namespace NegiCraftLauncher.Raster;

/// <summary>
/// 皮肤贴图的 UV 布局。平台中立版（<c>MinecraftSkinRender.SkinType</c> 是 GL 侧的对应物）。
///
/// <para>64x64（1.8+）走 <see cref="New"/> / <see cref="NewSlim"/>；64x32（1.7）必须走
/// <see cref="Old"/> —— 它的 V 按 32 归一化、手臂腿 UV 偏移完全不同，而且没有第二层覆盖贴图。</para>
/// </summary>
public enum SkinFormat
{
    Unknown = 0,
    New = 1,
    NewSlim = 2,
    Old = 3,
}
