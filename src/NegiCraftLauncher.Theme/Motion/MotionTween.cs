using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace NegiCraftLauncher.Theme;

/// <summary>
/// 单条属性过渡的句柄，也是<b>全项目唯一允许碰动画时钟的地方</b>。
///
/// <para>它把四份手写实现（ButtonMotion / SqueezeButton / Pop / PageTransition）里
/// 反复出现的六条规矩收成一份：</para>
/// <list type="number">
/// <item>读当前值用 <c>GetValue</c>（拿动画计算后的值），不是 LocalValue。</item>
/// <item>先把基值钉在当前显示值上再起动画 —— <c>FillBehavior.Stop</c> 结束后会回基值，
///   基值不等于终点就会跳一下。</item>
/// <item><c>FillBehavior.Stop</c> + Completed 里落地 + 清时钟。</item>
/// <item>版本守卫：被后一次过渡接管后，旧时钟的收尾不许再写属性。</item>
/// <item>同一 target + property 上新的过渡接管旧的（等价 SnapshotAndReplace 的语义），不排队。</item>
/// <item><see cref="MotionEngine.Enabled"/> 为 false 时直接落地，不建动画对象。</item>
/// </list>
///
/// <para>只支持 <c>double</c> 属性 —— 项目里的动画全是 Opacity / ScaleX/Y / TranslateX/Y / Angle。</para>
/// </summary>
public sealed class MotionTween
{
    private static readonly ConditionalWeakTable<object, Dictionary<DependencyProperty, MotionTween>> Live = new();

    private readonly DependencyObject _target;
    private readonly IAnimatable _animatable;
    private readonly DependencyProperty _property;
    private int _version;
    private bool _counted;

    private MotionTween(DependencyObject target, DependencyProperty property)
    {
        _target = target;
        _animatable = target as IAnimatable
            ?? throw new ArgumentException("target 必须实现 IAnimatable（UIElement 或 Animatable）", nameof(target));
        _property = property;
    }

    /// <summary>
    /// 从当前显示值过渡到 <paramref name="to"/>。
    /// <paramref name="from"/> 给出时改从它起步（等价旧代码的 <c>DoubleAnimation.From</c>，
    /// 用于"元素还没显示就先设起点"的场景，不必为了设起点去覆盖 LocalValue）。
    /// </summary>
    public static MotionTween Start(
        DependencyObject target, DependencyProperty property, double to,
        TimeSpan duration, KeySpline? curve = null, Action? completed = null, double? from = null)
    {
        var tween = new MotionTween(target, property);
        tween.Run([(duration, to)], curve, completed, from);
        return tween;
    }

    /// <summary>
    /// 多帧过渡。帧的时刻是<b>从过渡开始算的绝对时间</b>，必须严格递增
    /// （SqueezeButton 的回弹 35/145/265/385/510ms 就是这种形状）。
    /// </summary>
    public static MotionTween Keyframes(
        DependencyObject target, DependencyProperty property,
        IReadOnlyList<(TimeSpan At, double Value)> frames, KeySpline? curve = null,
        Action? completed = null, double? from = null)
    {
        var tween = new MotionTween(target, property);
        tween.Run(frames, curve, completed, from);
        return tween;
    }

    /// <summary>从当前显示值继续过渡到新目标（动画进行中改主意时用）。</summary>
    public void Retarget(double to, TimeSpan duration, KeySpline? curve = null, Action? completed = null)
        => Run([(duration, to)], curve, completed);

    /// <summary>
    /// 取消：把属性钉在当前显示值上（不是跳回起点）并释放时钟。
    /// 元素被重置/隐藏时调它，避免残留时钟继续驱动属性。
    /// 过渡已经结束（自己收尾过）时是无操作。
    /// </summary>
    public void Cancel()
    {
        _version++;
        if (!_counted)
        {
            Unregister();
            return;
        }
        var current = (double)_target.GetValue(_property);
        Settle(current);
        Unregister();
        EndCount();
    }

    private void Run(
        IReadOnlyList<(TimeSpan At, double Value)> frames, KeySpline? curve,
        Action? completed, double? from = null)
    {
        var version = Replace();
        var start = from ?? (double)_target.GetValue(_property);
        var last = frames.Count > 0 ? frames[^1].Value : start;
        var total = frames.Count > 0 ? frames[^1].At : TimeSpan.Zero;

        if (!MotionEngine.Enabled || frames.Count == 0 || total <= TimeSpan.Zero)
        {
            Settle(last);
            EndCount();
            completed?.Invoke();
            return;
        }

        _target.SetValue(_property, start);

        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (var (at, value) in frames)
        {
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(
                value,
                KeyTime.FromTimeSpan(MotionEngine.Scale(at)),
                curve ?? Motion.Enter));
        }

        var clock = animation.CreateClock();
        clock.Completed += (_, _) =>
        {
            // 版本对不上 = 已被后一次过渡接管：不落地（交给新的），但计数要释放。
            if (_version == version) Settle(last);
            EndCount();
            if (_version == version) completed?.Invoke();
        };

        BeginCount();
        _animatable.ApplyAnimationClock(_property, clock);
    }

    /// <summary>接管同一 target + property 上的旧过渡，返回本实例的版本号。</summary>
    private int Replace()
    {
        var map = Live.GetValue(_target, static _ => new Dictionary<DependencyProperty, MotionTween>());
        lock (map)
        {
            if (map.TryGetValue(_property, out var previous)) previous._version++;
            map[_property] = this;
            return ++_version;
        }
    }

    private void Unregister()
    {
        if (!Live.TryGetValue(_target, out var map)) return;
        lock (map)
        {
            if (map.TryGetValue(_property, out var current) && ReferenceEquals(current, this))
                map.Remove(_property);
        }
    }

    private void Settle(double value)
    {
        _target.SetValue(_property, value);
        _animatable.ApplyAnimationClock(_property, null);
    }

    private void BeginCount()
    {
        if (_counted) return;
        _counted = true;
        MotionEngine.TweenStarted();
    }

    private void EndCount()
    {
        if (!_counted) return;
        _counted = false;
        MotionEngine.TweenEnded();
    }
}
