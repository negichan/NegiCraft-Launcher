using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NegiCraftLauncher.App.Controls;

public static class Pop
{
    internal const int SlideOpenDurationMs = 260;
    internal const int SlideCloseDurationMs = 210;

    private static readonly ConditionalWeakTable<FrameworkElement, Runner> Runners = new();

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.RegisterAttached(
        "IsOpen", typeof(bool), typeof(Pop), new PropertyMetadata(false, OnIsOpenChanged));
    public static readonly DependencyProperty TriggerProperty = DependencyProperty.RegisterAttached(
        "Trigger", typeof(FrameworkElement), typeof(Pop), new PropertyMetadata(null));
    public static readonly DependencyProperty AnchorProperty = DependencyProperty.RegisterAttached(
        "Anchor", typeof(string), typeof(Pop), new PropertyMetadata("BottomLeft"));
    public static readonly DependencyProperty PinProperty = DependencyProperty.RegisterAttached(
        "Pin", typeof(string), typeof(Pop), new PropertyMetadata(""));
    public static readonly DependencyProperty PinGapProperty = DependencyProperty.RegisterAttached(
        "PinGap", typeof(double), typeof(Pop), new PropertyMetadata(6.0));
    public static readonly DependencyProperty SlideOnlyProperty = DependencyProperty.RegisterAttached(
        "SlideOnly", typeof(bool), typeof(Pop), new PropertyMetadata(false));

    public static bool GetIsOpen(DependencyObject target) => (bool)target.GetValue(IsOpenProperty);
    public static void SetIsOpen(DependencyObject target, bool value) => target.SetValue(IsOpenProperty, value);
    public static FrameworkElement? GetTrigger(DependencyObject target) => (FrameworkElement?)target.GetValue(TriggerProperty);
    public static void SetTrigger(DependencyObject target, FrameworkElement? value) => target.SetValue(TriggerProperty, value);
    public static string GetAnchor(DependencyObject target) => (string)target.GetValue(AnchorProperty);
    public static void SetAnchor(DependencyObject target, string value) => target.SetValue(AnchorProperty, value);
    public static string GetPin(DependencyObject target) => (string)target.GetValue(PinProperty);
    public static void SetPin(DependencyObject target, string value) => target.SetValue(PinProperty, value);
    public static double GetPinGap(DependencyObject target) => (double)target.GetValue(PinGapProperty);
    public static void SetPinGap(DependencyObject target, double value) => target.SetValue(PinGapProperty, value);
    public static bool GetSlideOnly(DependencyObject target) => (bool)target.GetValue(SlideOnlyProperty);
    public static void SetSlideOnly(DependencyObject target, bool value) => target.SetValue(SlideOnlyProperty, value);

    private static void OnIsOpenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        var runner = Runners.GetValue(element, static item => new Runner(item));
        if (args.NewValue is true) runner.Open();
        else runner.Close();
    }

    private sealed class Runner
    {
        private readonly FrameworkElement _target;
        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _position = new();
        private readonly TransformGroup _transform = new();
        private readonly BitmapCache _cache = new(1.1);
        private CacheMode? _priorCache;
        private bool _cacheApplied;
        private bool _visible;
        private int _version;
        private double _pinX, _pinY, _enterY;

        public Runner(FrameworkElement target)
        {
            _target = target;
            _transform.Children.Add(_scale);
            _transform.Children.Add(_position);
            target.Unloaded += (_, _) => Rest();
            target.Loaded += (_, _) =>
            {
                if (GetIsOpen(target)) Open();
            };
        }

        public void Open()
        {
            var version = ++_version;
            Measure();
            var slideOnly = GetSlideOnly(_target);
            if (!_visible)
            {
                _scale.ScaleX = _scale.ScaleY = slideOnly ? 1 : 0.94;
                _position.X = _pinX;
                _position.Y = _pinY + _enterY;
                _target.Opacity = 0;
            }
            _visible = true;
            _target.RenderTransform = _transform;
            _target.Clip = null;
            _target.IsHitTestVisible = true;
            if (!SystemParameters.ClientAreaAnimation || !_target.IsLoaded)
            {
                Settle();
                return;
            }
            CacheForAnimation();
            if (slideOnly)
            {
                Animate(_position, TranslateTransform.XProperty, version, (SlideOpenDurationMs, _pinX));
                Animate(_position, TranslateTransform.YProperty, version, (SlideOpenDurationMs, _pinY));
                Animate(_target, UIElement.OpacityProperty, version, (220, 1), (SlideOpenDurationMs, 1), completed: Settle);
                return;
            }
            Animate(_scale, ScaleTransform.ScaleXProperty, version, (150, 1.014), (220, 1));
            Animate(_scale, ScaleTransform.ScaleYProperty, version, (150, 1.014), (220, 1));
            Animate(_position, TranslateTransform.XProperty, version, (220, _pinX));
            Animate(_position, TranslateTransform.YProperty, version, (180, _pinY), (220, _pinY));
            Animate(_target, UIElement.OpacityProperty, version, (100, 1), (220, 1), completed: Settle);
        }

        public void Close()
        {
            var version = ++_version;
            _target.IsHitTestVisible = false;
            if (!_visible || !SystemParameters.ClientAreaAnimation || !_target.IsLoaded)
            {
                Rest();
                return;
            }
            _target.RenderTransform = _transform;
            CacheForAnimation();
            var slideOnly = GetSlideOnly(_target);
            var duration = slideOnly ? SlideCloseDurationMs : 110;
            Animate(_position, TranslateTransform.XProperty, version, (duration, _pinX));
            if (!slideOnly)
            {
                Animate(_scale, ScaleTransform.ScaleXProperty, version, (110, 0.96));
                Animate(_scale, ScaleTransform.ScaleYProperty, version, (110, 0.96));
            }
            var exitY = slideOnly ? Math.Sign(_enterY) * 12 : _enterY * 0.75;
            Animate(_position, TranslateTransform.YProperty, version, (duration, _pinY + exitY));
            Animate(_target, UIElement.OpacityProperty, version, (duration, 0), completed: Rest);
        }

        private void Animate(
            DependencyObject target, DependencyProperty property, int version,
            (int Ms, double Value) first, (int Ms, double Value)? second = null, Action? completed = null)
        {
            var current = (double)target.GetValue(property);
            var rest = second?.Value ?? first.Value;
            target.SetValue(property, current);
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(current, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            Add(first);
            if (second is { } next) Add(next);
            animation.Completed += (_, _) =>
            {
                if (_version != version) return;
                target.SetValue(property, rest);
                if (target is Animatable value) value.BeginAnimation(property, null);
                else ((UIElement)target).BeginAnimation(property, null);
                completed?.Invoke();
            };
            if (target is Animatable animatable)
                animatable.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
            else
                ((UIElement)target).BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);

            void Add((int Ms, double Value) frame) => animation.KeyFrames.Add(new SplineDoubleKeyFrame(
                frame.Value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.Ms)),
                GetSlideOnly(_target) ? new KeySpline(0.22, 0.55, 0.35, 1) : new KeySpline(0.2, 0.75, 0.25, 1)));
        }

        private void CacheForAnimation()
        {
            if (_cacheApplied) return;
            _priorCache = _target.CacheMode;
            _target.SetCurrentValue(UIElement.CacheModeProperty, _cache);
            _cacheApplied = true;
        }

        private void ClearClocks()
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _position.BeginAnimation(TranslateTransform.XProperty, null);
            _position.BeginAnimation(TranslateTransform.YProperty, null);
            _target.BeginAnimation(UIElement.OpacityProperty, null);
            if (_cacheApplied)
            {
                if (ReferenceEquals(_target.CacheMode, _cache))
                    _target.SetCurrentValue(UIElement.CacheModeProperty, _priorCache);
                _cacheApplied = false;
            }
        }

        private void Settle()
        {
            ClearClocks();
            _scale.ScaleX = _scale.ScaleY = 1;
            _position.X = _pinX;
            _position.Y = _pinY;
            _target.Opacity = 1;
            _target.RenderTransform = _pinX == 0 && _pinY == 0 ? null : _transform;
        }

        private void Rest()
        {
            ++_version;
            ClearClocks();
            _visible = false;
            _scale.ScaleX = _scale.ScaleY = 1;
            _position.X = _position.Y = 0;
            _target.RenderTransform = null;
            _target.Clip = null;
            _target.ClearValue(UIElement.OpacityProperty);
            _target.IsHitTestVisible = false;
        }

        private void Measure()
        {
            var (fx, fy) = AnchorFraction(GetAnchor(_target));
            _target.RenderTransformOrigin = new Point(fx, fy);
            _enterY = (fy >= 0.5 ? 1 : -1) * (GetSlideOnly(_target) ? 14 : 8);
            _pinX = _pinY = 0;
            var pin = GetPin(_target);
            var trigger = GetTrigger(_target);
            if (pin.Length == 0 || trigger is null || !trigger.IsVisible ||
                _target.Parent is not FrameworkElement host || _target.ActualWidth < 1 ||
                trigger.ActualWidth < 1 || trigger.ActualHeight < 1) return;

            try
            {
                var triggerAt = trigger.TransformToVisual(host).Transform(new Point());
                var popAt = _target.TransformToVisual(host).Transform(new Point());
                // Changing RenderTransform does not synchronously update the visual transform.
                // Remove its current offset analytically when reopening mid-animation.
                var origin = new Point(_target.ActualWidth * fx, _target.ActualHeight * fy);
                var matrix = _target.RenderTransform?.Value ?? Matrix.Identity;
                var transformed = matrix.Transform(new Point(-origin.X, -origin.Y));
                popAt -= new Vector(transformed.X + origin.X, transformed.Y + origin.Y);
                var width = _target.ActualWidth;
                var x = pin switch
                {
                    "UnderCenter" => triggerAt.X + (trigger.ActualWidth - width) / 2,
                    "UnderRight" => triggerAt.X + trigger.ActualWidth - width,
                    _ => triggerAt.X,
                };
                x = Math.Clamp(x, 12, Math.Max(12, host.ActualWidth - width - 12));
                _pinX = x - popAt.X;
                _pinY = triggerAt.Y + trigger.ActualHeight + GetPinGap(_target) - popAt.Y;
            }
            catch (InvalidOperationException)
            {
                _pinX = _pinY = 0;
            }
        }

        private static (double X, double Y) AnchorFraction(string anchor) => anchor switch
        {
            "BottomRight" => (1, 1),
            "BottomCenter" => (0.5, 1),
            "TopRight" => (1, 0),
            "TopCenter" => (0.5, 0),
            "TopLeft" => (0, 0),
            "Center" => (0.5, 0.5),
            _ => (0, 1),
        };
    }
}
