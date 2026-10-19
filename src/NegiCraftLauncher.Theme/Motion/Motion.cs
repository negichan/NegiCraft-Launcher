using System.Windows.Media.Animation;

namespace NegiCraftLauncher.Theme;

/// <summary>
/// 全项目动画的时长与曲线。改这里 = 改全局节奏，业务代码不要再写裸毫秒数。
///
/// <para>时长按"用户能感知的节奏"分档命名（Fast / Base / Slow / Slide），不按具体毫秒命名：
/// 以后要整体调速，改这里的数值就够，调用点不用动。</para>
///
/// <para>曲线三条，对应"元素从哪里来、到哪里去"的语义 —— 进入用 <see cref="Enter"/>（快出慢停），
/// 退出用 <see cref="Exit"/>（慢起快走），长距离滑动用 <see cref="Slide"/>（中段更匀）。</para>
/// </summary>
public static class Motion
{
    /// <summary>瞬时。只用于降级路径，业务代码不该直接引用。</summary>
    public static readonly TimeSpan Instant = TimeSpan.Zero;

    /// <summary>按下反馈：按钮压下去那一下要"弹"，再慢就发黏。</summary>
    public static readonly TimeSpan Press = TimeSpan.FromMilliseconds(60);

    /// <summary>按钮级微反馈：按下、hover 变色。</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(100);

    /// <summary>hover 过渡：比按下稍慢，跟手但不抢戏。</summary>
    public static readonly TimeSpan Hover = TimeSpan.FromMilliseconds(125);

    /// <summary>常规过渡：淡入、6~8px 位移。</summary>
    public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(160);

    /// <summary>明显位移：页面入场、浮层回弹。</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(240);

    /// <summary>大幅滑出：锚定浮层的打开。</summary>
    public static readonly TimeSpan Slide = TimeSpan.FromMilliseconds(260);

    /// <summary>元素进入：快出慢停（decelerate）。</summary>
    public static KeySpline Enter { get; } = new(0.2, 0.75, 0.25, 1);

    /// <summary>元素退出：慢起快走（accelerate）。</summary>
    public static KeySpline Exit { get; } = new(0.22, 0.7, 0.32, 1);

    /// <summary>长距离滑动：中段更匀，收尾不甩。配合时长 <see cref="Slide"/> 用。</summary>
    public static KeySpline Glide { get; } = new(0.22, 0.55, 0.35, 1);

    /// <summary>
    /// 缓动函数的等价物。只有非关键帧动画（<see cref="DoubleAnimation"/>）需要它；
    /// <see cref="MotionTween"/> 默认走 KeySpline，用不到这里。
    /// </summary>
    public static IEasingFunction EaseOut() => new CubicEase { EasingMode = EasingMode.EaseOut };
}
