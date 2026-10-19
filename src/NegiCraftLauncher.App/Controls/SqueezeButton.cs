using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NegiCraftLauncher.Theme;

namespace NegiCraftLauncher.App.Controls;

public sealed class SqueezeButton : Button
{
    private const double SqueezedWidth = 1.48;
    private const double SqueezedHeight = 0.50;

    /// <summary>
    /// 按下倾斜角在 [-TiltRange, +TiltRange] 之间随机取，每次按压都不一样。
    /// </summary>
    private const double TiltRange = 7;

    private readonly ScaleTransform _scale = new();
    private readonly RotateTransform _tilt = new();
    private FrameworkElement? _visual;

    /// <summary>
    /// 三条属性的在飞过渡。用字段持有是为了 <see cref="ResetMotion"/> 能主动 Cancel，
    /// 元素隐藏/卸载时不留下还在驱动属性的动画时钟。
    /// </summary>
    private MotionTween? _tweenScaleX;
    private MotionTween? _tweenScaleY;
    private MotionTween? _tweenTilt;

    /// <summary>
    /// 本次按压实际使用的倾斜角。松手回弹的第一帧要接这个值，
    /// 不能重新随机 —— 否则抬手瞬间角度会跳变。
    /// </summary>
    private double _squeezedTilt;

    public SqueezeButton()
    {
        Unloaded += (_, _) => ResetMotion();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) ResetMotion();
        };
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        ResetMotion();
        base.OnContentChanged(oldContent, newContent);
        _visual = newContent as FrameworkElement;
        if (_visual is null) return;

        // Deform only the artwork so the hit area and sidebar layout stay still.
        _visual.RenderTransformOrigin = new Point(0.5, 0.55);
        var transform = new TransformGroup();
        transform.Children.Add(_scale);
        transform.Children.Add(_tilt);
        _visual.RenderTransform = transform;
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        if (_visual is null || !IsLoaded || !IsVisible) return;

        if (IsPressed)
        {
            // [-TiltRange, +TiltRange] 闭区间均匀随机，每次按压换个方向倒。
            _squeezedTilt = TiltRange * (2 * Random.Shared.NextDouble() - 1);
            _tweenScaleX = MotionTween.Start(
                _scale, ScaleTransform.ScaleXProperty, SqueezedWidth, Motion.Press, Motion.Exit);
            _tweenScaleY = MotionTween.Start(
                _scale, ScaleTransform.ScaleYProperty, SqueezedHeight, Motion.Press, Motion.Exit);
            _tweenTilt = MotionTween.Start(
                _tilt, RotateTransform.AngleProperty, _squeezedTilt, Motion.Press, Motion.Exit);
        }
        else
        {
            Rebound();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.IsRepeat && IsEnabled) Rebound();
        base.OnKeyDown(e);
    }

    private void Rebound()
    {
        if (_visual is null || !IsLoaded || !IsVisible) return;

        // 帧长按"松手时刻"算绝对值，与按压时长无关：短按只是少走一段，
        // 松手后这套振荡形状始终一致。
        _tweenScaleX = MotionTween.Keyframes(
            _scale, ScaleTransform.ScaleXProperty,
            Frames(SqueezedWidth, 0.76, 1.18, 0.94, 1), Motion.Exit);
        _tweenScaleY = MotionTween.Keyframes(
            _scale, ScaleTransform.ScaleYProperty,
            Frames(SqueezedHeight, 1.42, 0.79, 1.10, 1), Motion.Exit);

        // 回弹过冲走按压的反方向：倾角固定 -7° 时曲线写死 +6.5 / -3 / +1.2；
        // 现在左右随机，整条振荡要按方向镜像过去 —— 否则倒向右（正角）时过冲同向叠加，
        // 回弹那一下会发肉。sign = 0（随机到 0 或键盘首触发）就不摆，直接回正。
        var sign = Math.Sign(_squeezedTilt);
        _tweenTilt = MotionTween.Keyframes(
            _tilt, RotateTransform.AngleProperty,
            Frames(_squeezedTilt, -6.5 * sign, 3.0 * sign, -1.2 * sign, 0), Motion.Exit);
    }

    /// <summary>
    /// 回弹振荡的五个台阶：先砸回压下的位置，再反向过冲、回落、余振、归位。
    /// </summary>
    private static (TimeSpan At, double Value)[] Frames(
        double squeezed, double overshoot, double settle, double ripple, double rest) =>
    [
        (TimeSpan.FromMilliseconds(35), squeezed),
        (TimeSpan.FromMilliseconds(145), overshoot),
        (TimeSpan.FromMilliseconds(265), settle),
        (TimeSpan.FromMilliseconds(385), ripple),
        (TimeSpan.FromMilliseconds(510), rest),
    ];

    private void ResetMotion()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        _tweenScaleX?.Cancel();
        _tweenScaleY?.Cancel();
        _tweenTilt?.Cancel();
        _tweenScaleX = _tweenScaleY = _tweenTilt = null;
        _scale.ScaleX = _scale.ScaleY = 1;
        _tilt.Angle = 0;
    }
}
