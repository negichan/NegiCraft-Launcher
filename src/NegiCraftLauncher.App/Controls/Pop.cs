using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using NegiCraftLauncher.Theme;

namespace NegiCraftLauncher.App.Controls;

public static class Pop
{
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
    public static readonly DependencyProperty TipProperty = DependencyProperty.RegisterAttached(
        "Tip", typeof(bool), typeof(Pop), new PropertyMetadata(false));
    public static readonly DependencyProperty TipAxisProperty = DependencyProperty.RegisterAttached(
        "TipAxis", typeof(string), typeof(Pop), new PropertyMetadata("Y"));

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
    public static bool GetTip(DependencyObject target) => (bool)target.GetValue(TipProperty);
    public static void SetTip(DependencyObject target, bool value) => target.SetValue(TipProperty, value);
    public static string GetTipAxis(DependencyObject target) => (string)target.GetValue(TipAxisProperty);
    public static void SetTipAxis(DependencyObject target, string value) => target.SetValue(TipAxisProperty, value);

    private static void OnIsOpenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        var runner = Runners.GetValue(element, static item => new Runner(item));
        if (args.NewValue is true) runner.Open();
        else runner.Close();
    }

    private sealed class Runner
    {
        /// <summary>提示条模式的位移量：够看出"从哪儿出来"，又不会让小块头上下窜。</summary>
        private const double TipTravel = 6;

        private readonly FrameworkElement _target;
        private readonly ScaleTransform _scale = new();
        private readonly TranslateTransform _position = new();
        private readonly TransformGroup _transform = new();
        private readonly BitmapCache _cache = new(1.1);

        /// <summary>
        /// 收合后应该回到的透明度。不能靠 <c>ClearValue</c> —— 调用点的"隐藏态"
        /// 可能写在样式里（PopoverStyle 的 Opacity=0），清掉本地值不等于回到隐藏态。
        /// </summary>
        private readonly double _restOpacity;

        private CacheMode? _priorCache;
        private bool _cacheApplied;
        private bool _visible;
        private double _pinX, _pinY, _enterX, _enterY;

        /// <summary>在飞的过渡句柄，只用于取消 —— 属性落值一律交给 <see cref="MotionTween"/>。</summary>
        private MotionTween? _tweenScaleX, _tweenScaleY, _tweenX, _tweenY, _tweenOpacity;

        public Runner(FrameworkElement target)
        {
            _target = target;
            _restOpacity = target.Opacity;
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
            Measure();
            var tip = GetTip(_target);
            if (!_visible)
            {
                _scale.ScaleX = _scale.ScaleY = tip ? 1 : 0.94;
                _position.X = _pinX + _enterX;
                _position.Y = _pinY + _enterY;
                _target.Opacity = 0;
            }
            _visible = true;
            _target.RenderTransform = _transform;
            _target.Clip = null;
            _target.IsHitTestVisible = true;
            if (!MotionEngine.Enabled || !_target.IsLoaded)
            {
                Settle();
                return;
            }
            if (tip)
            {
                // 提示条只做"淡入 + 6px 归位"：它挂在鼠标边上的时间很短，
                // 加缩放或缓存只会让它显脏。
                _tweenX = MotionTween.Start(
                    _position, TranslateTransform.XProperty, _pinX, Motion.Hover, Motion.Enter);
                _tweenY = MotionTween.Start(
                    _position, TranslateTransform.YProperty, _pinY, Motion.Hover, Motion.Enter);
                _tweenOpacity = MotionTween.Start(
                    _target, UIElement.OpacityProperty, 1, Motion.Hover, Motion.Enter, Settle);
                return;
            }
            CacheForAnimation();
            var curve = Motion.Enter;
            // 缩放的轻微过冲（0.94 → 1.014 → 1）是浮层"弹出来"的关键，去掉就变生硬。
            _tweenScaleX = MotionTween.Keyframes(
                _scale, ScaleTransform.ScaleXProperty,
                [(Motion.Base, 1.014), (Motion.Slow, 1)], curve);
            _tweenScaleY = MotionTween.Keyframes(
                _scale, ScaleTransform.ScaleYProperty,
                [(Motion.Base, 1.014), (Motion.Slow, 1)], curve);
            _tweenX = MotionTween.Start(_position, TranslateTransform.XProperty, _pinX, Motion.Slow, curve);
            _tweenY = MotionTween.Start(_position, TranslateTransform.YProperty, _pinY, Motion.Slow, curve);
            // 点开就要立刻见字，所以淡入用最短档；靠第二帧把时长撑到和位移同刻结束，
            // Settle 只在这一刻跑一次。
            _tweenOpacity = MotionTween.Keyframes(
                _target, UIElement.OpacityProperty, [(Motion.Fast, 1), (Motion.Slow, 1)], curve, Settle);
        }

        public void Close()
        {
            _target.IsHitTestVisible = false;
            if (!_visible || !MotionEngine.Enabled || !_target.IsLoaded)
            {
                Rest();
                return;
            }
            _target.RenderTransform = _transform;
            if (GetTip(_target))
            {
                _tweenX = MotionTween.Start(
                    _position, TranslateTransform.XProperty, _pinX + _enterX, Motion.Fast, Motion.Exit);
                _tweenY = MotionTween.Start(
                    _position, TranslateTransform.YProperty, _pinY + _enterY, Motion.Fast, Motion.Exit);
                _tweenOpacity = MotionTween.Start(
                    _target, UIElement.OpacityProperty, 0, Motion.Fast, Motion.Exit, Rest);
                return;
            }
            CacheForAnimation();
            var curve = Motion.Exit;
            var duration = Motion.Fast;
            _tweenX = MotionTween.Start(_position, TranslateTransform.XProperty, _pinX, duration, curve);
            _tweenScaleX = MotionTween.Start(_scale, ScaleTransform.ScaleXProperty, 0.96, duration, curve);
            _tweenScaleY = MotionTween.Start(_scale, ScaleTransform.ScaleYProperty, 0.96, duration, curve);
            var exitY = _enterY * 0.75;
            _tweenY = MotionTween.Start(_position, TranslateTransform.YProperty, _pinY + exitY, duration, curve);
            _tweenOpacity = MotionTween.Start(_target, UIElement.OpacityProperty, 0, duration, curve, Rest);
        }

        private void CacheForAnimation()
        {
            if (_cacheApplied) return;
            _priorCache = _target.CacheMode;
            _target.SetCurrentValue(UIElement.CacheModeProperty, _cache);
            _cacheApplied = true;
        }

        private void CancelTweens()
        {
            _tweenScaleX?.Cancel();
            _tweenScaleY?.Cancel();
            _tweenX?.Cancel();
            _tweenY?.Cancel();
            _tweenOpacity?.Cancel();
            _tweenScaleX = _tweenScaleY = _tweenX = _tweenY = _tweenOpacity = null;
        }

        private void ClearClocks()
        {
            CancelTweens();
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
            ClearClocks();
            _visible = false;
            _scale.ScaleX = _scale.ScaleY = 1;
            _position.X = _position.Y = 0;
            _target.RenderTransform = null;
            _target.Clip = null;
            _target.Opacity = _restOpacity;
            _target.IsHitTestVisible = false;
        }

        private void Measure()
        {
            var (fx, fy) = AnchorFraction(GetAnchor(_target));
            _target.RenderTransformOrigin = new Point(fx, fy);
            _pinX = _pinY = _enterX = 0;
            if (GetTip(_target))
            {
                // 提示条不做锚定定位：它靠对齐方式固定位置，只需要一个"从哪边冒出来"的偏移。
                if (GetTipAxis(_target) == "X") _enterX = -TipTravel;
                else _enterY = -TipTravel;
                return;
            }
            _enterY = fy >= 0.5 ? 8 : -8;
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
