using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
    private int _motionVersion;

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
            var version = ++_motionVersion;
            // [-TiltRange, +TiltRange] 闭区间均匀随机，每次按压换个方向倒。
            _squeezedTilt = TiltRange * (2 * Random.Shared.NextDouble() - 1);
            AnimatePress(_scale, ScaleTransform.ScaleXProperty, SqueezedWidth, version);
            AnimatePress(_scale, ScaleTransform.ScaleYProperty, SqueezedHeight, version);
            AnimatePress(_tilt, RotateTransform.AngleProperty, _squeezedTilt, version);
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

    private void AnimatePress(Animatable target, DependencyProperty property, double value, int version)
    {
        var current = (double)target.GetValue(property);
        target.SetValue(property, value);
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            return;
        }

        var animation = new DoubleAnimation(current, value, TimeSpan.FromMilliseconds(60))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        animation.Completed += (_, _) =>
        {
            if (_motionVersion == version) target.BeginAnimation(property, null);
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void Rebound()
    {
        if (_visual is null || !IsLoaded || !IsVisible) return;
        var version = ++_motionVersion;
        AnimateRelease(_scale, ScaleTransform.ScaleXProperty, version, SqueezedWidth, 0.76, 1.18, 0.94, 1);
        AnimateRelease(_scale, ScaleTransform.ScaleYProperty, version, SqueezedHeight, 1.42, 0.79, 1.10, 1);
        // 回弹过冲走按压的反方向：倾角固定 -7° 时曲线写死 +6.5 / -3 / +1.2；
        // 现在左右随机，整条振荡要按方向镜像过去 —— 否则倒向右（正角）时过冲同向叠加，
        // 回弹那一下会发肉。sign = 0（随机到 0 或键盘首触发）就不摆，直接回正。
        var sign = Math.Sign(_squeezedTilt);
        AnimateRelease(_tilt, RotateTransform.AngleProperty, version, _squeezedTilt,
            -6.5 * sign, 3.0 * sign, -1.2 * sign, 0);
    }

    private void AnimateRelease(
        Animatable target, DependencyProperty property, int version,
        double squeezed, double overshoot, double settle, double ripple, double rest)
    {
        var current = (double)target.GetValue(property);
        target.SetValue(property, rest);
        if (!SystemParameters.ClientAreaAnimation)
        {
            target.BeginAnimation(property, null);
            return;
        }

        // A short click still gets a squeeze; a new press replaces this clock
        // from its current value instead of queuing another bounce.
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        AddFrame(35, squeezed);
        AddFrame(145, overshoot);
        AddFrame(265, settle);
        AddFrame(385, ripple);
        AddFrame(510, rest);
        animation.Completed += (_, _) =>
        {
            if (_motionVersion == version) target.BeginAnimation(property, null);
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);

        void AddFrame(int milliseconds, double value) =>
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(
                value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                new KeySpline(0.22, 0.7, 0.32, 1)));
    }

    private void ResetMotion()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        ++_motionVersion;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _tilt.BeginAnimation(RotateTransform.AngleProperty, null);
        _scale.ScaleX = _scale.ScaleY = 1;
        _tilt.Angle = 0;
    }
}
