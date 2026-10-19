using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using NegiCraftLauncher.Theme;

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
        private MotionTween? _tweenFade, _tweenMove;

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
            if (!MotionEngine.Enabled || GetSuppressEnterAnimation(_target)) return;

            // A parent transition already moves this content; avoid invalidating its cache.
            for (var parent = VisualTreeHelper.GetParent(_target); parent is not null;
                 parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is FrameworkElement element &&
                    Runners.TryGetValue(element, out var runner) && runner._active)
                    return;
            }

            _active = true;
            _priorCache = _target.CacheMode;
            _target.SetCurrentValue(UIElement.CacheModeProperty, _cache);
            // 起点交给 from 参数，不去写 Opacity/TranslateY 的 LocalValue —— 否则会踩掉样式或绑定给的值。
            _tweenFade = MotionTween.Start(
                _target, UIElement.OpacityProperty, _target.Opacity, Motion.Base, Motion.Enter, from: 0);
            _tweenMove = MotionTween.Start(
                _position, TranslateTransform.YProperty, 0, Motion.Slow, Motion.Enter, Reset, from: 6);
        }

        public void Reset()
        {
            _tweenFade?.Cancel();
            _tweenMove?.Cancel();
            _tweenFade = _tweenMove = null;
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
