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
            // 悬停只换底色，不再把控件放大/上提 —— 整行、整卡跟着缩放会让人以为点错了东西。
            var scale = pressed ? 0.965 : 1;
            var y = pressed ? 0.8 : 0;
            var duration = pressed ? Motion.Press : Motion.Base;
            _scaleX = Retarget(_scaleX, _scale, ScaleTransform.ScaleXProperty, scale, duration);
            _scaleY = Retarget(_scaleY, _scale, ScaleTransform.ScaleYProperty, scale, duration);
            _offsetY = Retarget(_offsetY, _position, TranslateTransform.YProperty, y, duration);
        }

        /// <summary>
        /// 已经站在目标值上就**不要起 tween**。
        ///
        /// <para>为什么重要：<c>ButtonMotion</c> 挂在**每一个**按钮的模板根上
        /// （见 <c>Theme/Controls.xaml</c>），而 <c>Update()</c> 是被 <c>IsVisibleChanged</c>
        /// 触发的 —— 换一页，一屏按钮全部"变可见"，每个都无条件起三条 tween。
        /// 那些 tween 的目标值就是当前位置（既没按也没悬停 ⇒ 回到 1 和 0），
        /// 视觉上什么都不会发生，但<b>动画时钟照样每帧在跑</b>。</para>
        ///
        /// <para>实测（Release / tier2 / 1300x840 / DPI 2x，动画强制打开）：换 6 次页
        /// 期间新起的过渡条数 **492 → 12**（见 <c>MotionEngine.StartedCount</c> 与
        /// 探针的 <c>--ui-performance</c>）。条数是确定的，帧率不是 ——
        /// 同一份二进制连跑 pages 在 32~35fps 之间抖（见 docs/motion-engine.md
        /// 记的 ±40% 跨进程波动），单看 fps 判不出这个改动。</para>
        ///
        /// <para>注意不是简单地"没按没悬停就不动"：从悬停状态退回静止是要动的
        /// （回到 1 和 0），那时当前值 ≠ 目标值，这条自然就会起 tween。
        /// 判据是"当前值和目标值一样吗"，不是"有没有按或悬停"。</para>
        /// </summary>
        private static MotionTween? Retarget(
            MotionTween? previous, Animatable target, DependencyProperty property,
            double to, TimeSpan duration)
        {
            // 旧的先收掉：它可能还在飞（上一帧的悬停动画），留着会和新的抢同一个属性。
            previous?.Cancel();
            if (Math.Abs((double)target.GetValue(property) - to) < 0.0001) return null;
            return MotionTween.Start(target, property, to, duration, Motion.Enter);
        }

        public void Reset()
        {
            _scaleX?.Cancel();
            _scaleY?.Cancel();
            _offsetY?.Cancel();
            _scaleX = _scaleY = _offsetY = null;
            _scale.ScaleX = _scale.ScaleY = 1;
            _position.Y = 0;
        }
    }
}
