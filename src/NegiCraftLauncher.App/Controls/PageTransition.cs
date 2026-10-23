using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using NegiCraftLauncher.Theme;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Controls;

/// <summary>
/// 页面入场过渡：淡入 + 按方向滑入。
///
/// <para><b>与 Avalonia 侧是对照的两份</b>（见 <c>App.Avalonia/Views/PageTransition.cs</c>）：
/// 同样的方向判据（<see cref="PageOrder"/>，共享层）、同样的时长与曲线、同样的位移距离。</para>
///
/// <para><b>方向在动画开始的那一刻从 DataContext 读</b>，而不是靠绑定预先写进附加属性 ——
/// 后者要靠"方向通知先于可见性通知到达"才成立，那是生成代码的顺序细节，脆。
/// 直接读 VM 就没有这个依赖。</para>
///
/// <para><b>为什么只有进场</b>：页面是叠在同一个容器里的兄弟节点，靠可见性切换，
/// 旧页立即消失，没有退场可做。</para>
/// </summary>
public static class PageTransition
{
    private static readonly ConditionalWeakTable<FrameworkElement, Runner> Runners = new();

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PageTransition), new PropertyMetadata(false, Changed));

    /// <summary>位移距离（DIP）。只淡入时忽略。</summary>
    public const double Distance = 16.0;

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

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
            if (!MotionEngine.Enabled) return;

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

            // 方向决定往哪边挪：FromAbove 表示"正在往侧栏上方走"，新页就从上方进来。
            var direction = (_target.DataContext as MainWindowViewModel)?.LastPageDirection
                            ?? PageDirection.None;
            var offset = direction switch
            {
                PageDirection.FromAbove => -Distance,
                PageDirection.FromBelow => Distance,
                // 首页只淡入：3D 皮肤模型会跟着飘，主脸不该晃。
                _ => 0.0,
            };

            // 起点交给 from 参数，不去写 Opacity/TranslateY 的 LocalValue —— 否则会踩掉样式或绑定给的值。
            //
            // 收尾（Reset）挂在**跑得久的那条**上：位移 240ms 比淡入 160ms 长，
            // 挂错会让滑动在 160ms 处被掐断（只走了一半就弹回去）。
            // 位移压根没起的时候（首页只淡入），收尾就挂在淡入上。
            _tweenFade = MotionTween.Start(
                _target, UIElement.OpacityProperty, _target.Opacity, Motion.Base, Motion.Enter,
                offset == 0 ? Reset : null, from: 0);
            if (offset != 0)
            {
                _tweenMove = MotionTween.Start(
                    _position, TranslateTransform.YProperty, 0, Motion.Slow, Motion.Enter, Reset, from: offset);
            }
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
