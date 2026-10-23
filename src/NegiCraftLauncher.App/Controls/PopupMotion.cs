using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NegiCraftLauncher.Theme;

namespace NegiCraftLauncher.App.Controls;

/// <summary>
/// 让一个 <see cref="Popup"/> 的出场走项目自己的动效调度，而不是 WPF 的 <c>PopupAnimation</c>。
///
/// <para><b>为什么不能直接用 <c>PopupAnimation="Fade"</c></b>：那是 WPF 内建的窗口淡入，
/// 时长和曲线由系统定，既不经过 <see cref="MotionTween"/>，也不受
/// <see cref="MotionEngine.Enabled"/> / <see cref="MotionEngine.SpeedScale"/> 管 ——
/// 用户在设置里选「关掉界面动画」时它照样淡入，选「总是开」时它也还是系统那个节奏。
/// 而且它跟 Avalonia 那边对不上（那边要自己实现一套）。所以统一改成这里：出场一律走
/// <see cref="MotionTween.Keyframes"/>，节奏取 <see cref="Motion"/> 的 token。</para>
///
/// <para><b>形状与 <see cref="Pop"/> 的普通浮层一致</b>：0.94 → 1.014（轻微过冲）→ 1 的缩放
/// 叠一层淡入，位移不做（Popup 自己已经定好位置，再叠位移会和窗口边缘打架）。
/// 缩放原点默认右上角 —— 现有的浮层都挂在右对齐的按钮上，从那一角往外长才像是从按钮里出来的。</para>
///
/// <para>关掉动画（或元素还没载入）时直接落到终态，不建动画对象。</para>
/// </summary>
public static class PopupMotion
{
    private static readonly ConditionalWeakTable<Popup, Runner> Runners = new();

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(PopupMotion), new PropertyMetadata(false, OnEnableChanged));

    /// <summary>缩放原点：<c>TopRight</c>（默认）/ <c>TopLeft</c> / <c>BottomRight</c> / <c>BottomLeft</c> / <c>Center</c>。</summary>
    public static readonly DependencyProperty OriginProperty = DependencyProperty.RegisterAttached(
        "Origin", typeof(string), typeof(PopupMotion), new PropertyMetadata("TopRight"));

    public static bool GetEnable(DependencyObject target) => (bool)target.GetValue(EnableProperty);
    public static void SetEnable(DependencyObject target, bool value) => target.SetValue(EnableProperty, value);
    public static string GetOrigin(DependencyObject target) => (string)target.GetValue(OriginProperty);
    public static void SetOrigin(DependencyObject target, string value) => target.SetValue(OriginProperty, value);

    private static void OnEnableChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Popup popup) return;
        // 订阅只建一次：附加属性被重复赋值时不要再挂一遍事件。
        Runners.GetValue(popup, static item => new Runner(item));
    }

    private static Point OriginFraction(string origin) => origin switch
    {
        "TopLeft" => new Point(0, 0),
        "BottomRight" => new Point(1, 1),
        "BottomLeft" => new Point(0, 1),
        "Center" => new Point(0.5, 0.5),
        _ => new Point(1, 0),
    };

    private sealed class Runner
    {
        /// <summary>起手缩到多小。再小就像"从无到有"，浮层该是"从按钮里长出来"。</summary>
        private const double RestScale = 0.94;

        /// <summary>过冲峰值。到 1 之后多走一点点再回来，是"弹出来"的关键。</summary>
        private const double Overshoot = 1.014;

        private readonly Popup _popup;
        private readonly ScaleTransform _scale = new();

        private MotionTween? _scaleX, _scaleY, _opacity;

        public Runner(Popup popup)
        {
            _popup = popup;
            _popup.Opened += (_, _) => Play();
            _popup.Closed += (_, _) => Rest();
        }

        private void Play()
        {
            if (_popup.Child is not FrameworkElement child) return;

            child.RenderTransformOrigin = OriginFraction(GetOrigin(_popup));

            if (!GetEnable(_popup) || !MotionEngine.Enabled || !child.IsLoaded)
            {
                Settle(child);
                return;
            }

            var curve = Motion.Enter;
            _scale.ScaleX = _scale.ScaleY = RestScale;
            child.RenderTransform = _scale;
            child.Opacity = 0;

            _scaleX = MotionTween.Keyframes(
                _scale, ScaleTransform.ScaleXProperty,
                [(Motion.Base, Overshoot), (Motion.Slow, 1)], curve);
            _scaleY = MotionTween.Keyframes(
                _scale, ScaleTransform.ScaleYProperty,
                [(Motion.Base, Overshoot), (Motion.Slow, 1)], curve);

            // 淡入用最短档、靠第二帧把时长撑到与缩放同刻结束，落地只跑一次 —— 与 Pop 同一套写法：
            // 打开的第一眼就要看到内容，慢慢浮现反而像卡了。
            _opacity = MotionTween.Keyframes(
                child, UIElement.OpacityProperty, [(Motion.Fast, 1), (Motion.Slow, 1)], curve,
                () => Settle(child));
        }

        /// <summary>落到终态。已经收尾过就是幂等的。</summary>
        private void Settle(FrameworkElement child)
        {
            Cancel();
            child.Opacity = 1;
            _scale.ScaleX = _scale.ScaleY = 1;
            child.RenderTransform = null;
        }

        /// <summary>Popup 关掉后把状态清干净，免得下一次打开的首帧带着上次的残影。</summary>
        private void Rest()
        {
            if (_popup.Child is FrameworkElement child) Settle(child);
        }

        private void Cancel()
        {
            _scaleX?.Cancel();
            _scaleY?.Cancel();
            _opacity?.Cancel();
            _scaleX = _scaleY = _opacity = null;
        }
    }
}
