using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace NegiCraftLauncher.App.Controls;

/// <summary>
/// UniformGrid，但 layout clip 四周多留 <see cref="Room"/> 的空间。
///
/// <para>WPF 会在面板的 DesiredSize 超出排列槽位时给它自己套一层 layout clip。UniformGrid
/// 的格宽是除出来的小数，最后总会多出零点几个像素 —— 于是这层裁剪一直存在，而它的左/上边
/// 正好压在卡片边上。按钮的 hover 反馈会把卡片放大 1.012（见 <c>ButtonMotion</c>），
/// 左上角就会被削掉一两个像素。这里只把裁剪框向外扩，布局和卡片落点都不动。</para>
/// </summary>
public sealed class HoverRoomUniformGrid : UniformGrid
{
    /// <summary>卡片最宽约 361 DIP 时单边只涨 2.2px；4 是余量。</summary>
    private const double Room = 4;

    protected override Geometry GetLayoutClip(Size layoutSlotSize)
    {
        var clip = base.GetLayoutClip(layoutSlotSize);
        if (clip is null) return null!;

        var bounds = clip.Bounds;
        bounds.Inflate(Room, Room);
        return new RectangleGeometry(bounds);
    }
}
