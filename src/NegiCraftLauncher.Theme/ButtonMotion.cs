using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NegiCraftLauncher.Theme;

public static class ButtonMotion
{
    private static readonly ConditionalWeakTable<FrameworkElement, Runner> Runners = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(ButtonMotion), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.RegisterAttached(
        "Pressed", typeof(bool), typeof(ButtonMotion), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty HoveredProperty = DependencyProperty.RegisterAttached(
        "Hovered", typeof(bool), typeof(ButtonMotion), new PropertyMetadata(false, Changed));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetPressed(DependencyObject target) => (bool)target.GetValue(PressedProperty);
    public static void SetPressed(DependencyObject target, bool value) => target.SetValue(PressedProperty, value);
    public static bool GetHovered(DependencyObject target) => (bool)target.GetValue(HoveredProperty);
    public static void SetHovered(DependencyObject target, bool value) => target.SetValue(HoveredProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if (GetEnabled(element))
            Runners.GetValue(element, static item => new Runner(item)).Update();
        else if (Runners.TryGetValue(element, out var runner))
            runner.Reset();
    }

    private sealed class Runner
    {
        private readonly FrameworkElement _target;
        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _position = new();
        private MotionTween? _scaleX;
        private MotionTween? _scaleY;
        private MotionTween? _offsetY;

        public Runner(FrameworkElement target)
        {
            _target = target;
            var transform = new TransformGroup();
            transform.Children.Add(_scale);
            transform.Children.Add(_position);
            target.RenderTransform = transform;
            target.RenderTransformOrigin = new Point(0.5, 0.5);
            target.Loaded += (_, _) => Update();
            target.Unloaded += (_, _) => Reset();
            target.IsVisibleChanged += (_, _) =>
            {
                if (target.IsVisible) Update();
                else Reset();
            };
        }

        public void Update()
        {
            if (!_target.IsLoaded || !_target.IsVisible || !GetEnabled(_target))
            {
                Reset();
                return;
            }
            var pressed = GetPressed(_target);
            var hovered = GetHovered(_target);
            var scale = pressed ? 0.965 : hovered ? 1.012 : 1;
            var y = pressed ? 0.8 : hovered ? -0.5 : 0;
            var duration = pressed ? Motion.Press : hovered ? Motion.Hover : Motion.Base;
            _scaleX = MotionTween.Start(_scale, ScaleTransform.ScaleXProperty, scale, duration, Motion.Enter);
            _scaleY = MotionTween.Start(_scale, ScaleTransform.ScaleYProperty, scale, duration, Motion.Enter);
            _offsetY = MotionTween.Start(_position, TranslateTransform.YProperty, y, duration, Motion.Enter);
        }

        public void Reset()
        {
            _scaleX?.Cancel();
            _scaleY?.Cancel();
            _offsetY?.Cancel();
            _scale.ScaleX = _scale.ScaleY = 1;
            _position.Y = 0;
        }
    }
}
