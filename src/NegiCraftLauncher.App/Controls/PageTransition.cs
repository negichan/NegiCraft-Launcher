using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NegiCraftLauncher.App.Controls;

public static class PageTransition
{
    private static readonly ConditionalWeakTable<FrameworkElement, Runner> Runners = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PageTransition), new PropertyMetadata(false, Changed));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    public static readonly DependencyProperty SuppressEnterAnimationProperty = DependencyProperty.RegisterAttached(
        "SuppressEnterAnimation", typeof(bool), typeof(PageTransition),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetSuppressEnterAnimation(DependencyObject target) => (bool)target.GetValue(SuppressEnterAnimationProperty);
    public static void SetSuppressEnterAnimation(DependencyObject target, bool value) => target.SetValue(SuppressEnterAnimationProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        if (args.NewValue is true)
            Runners.GetValue(element, static item => new Runner(item)).Enter();
        else if (Runners.TryGetValue(element, out var runner))
            runner.Reset();
    }

    private sealed class Runner
    {
        private readonly FrameworkElement _target;
        private readonly TranslateTransform _position = new();
        private readonly BitmapCache _cache = new();
        private CacheMode? _priorCache;
        private bool _active;
        private int _version;

        public Runner(FrameworkElement target)
        {
            _target = target;
            target.RenderTransform = _position;
            target.Loaded += (_, _) => Enter();
            target.Unloaded += (_, _) => Reset();
            target.IsVisibleChanged += (_, _) =>
            {
                if (target.IsVisible) Enter();
                else Reset();
            };
        }

        public void Enter()
        {
            if (!_target.IsLoaded || !_target.IsVisible || !GetEnabled(_target)) return;
            Reset();
            if (!SystemParameters.ClientAreaAnimation || GetSuppressEnterAnimation(_target)) return;

            // A parent transition already moves this content; avoid invalidating its cache.
            for (var parent = VisualTreeHelper.GetParent(_target); parent is not null;
                 parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement element &&
                    Runners.TryGetValue(element, out var runner) && runner._active)
                    return;
            }

            var version = ++_version;
            _active = true;
            _priorCache = _target.CacheMode;
            _target.SetCurrentValue(UIElement.CacheModeProperty, _cache);
            _position.Y = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, _target.Opacity, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            var move = new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            move.Completed += (_, _) =>
            {
                if (_version == version) Reset();
            };
            _target.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            _position.BeginAnimation(TranslateTransform.YProperty, move, HandoffBehavior.SnapshotAndReplace);
        }

        public void Reset()
        {
            ++_version;
            _target.BeginAnimation(UIElement.OpacityProperty, null);
            _position.BeginAnimation(TranslateTransform.YProperty, null);
            _position.Y = 0;
            if (_active)
            {
                if (ReferenceEquals(_target.CacheMode, _cache))
                    _target.SetCurrentValue(UIElement.CacheModeProperty, _priorCache);
            }
            _active = false;
        }
    }
}
