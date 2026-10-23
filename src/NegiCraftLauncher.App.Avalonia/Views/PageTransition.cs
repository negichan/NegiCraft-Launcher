using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Avalonia.Views;

/// <summary>
/// 页面入场过渡：淡入 + 按方向滑入。
///
/// <para><b>与 WPF 侧是对照的两份</b>（见 <c>App/Controls/PageTransition.cs</c>）：
/// 同样的方向判据（<see cref="PageOrder"/>，共享层）、同样的时长与曲线、同样的位移距离。</para>
///
/// <para><b>为什么在代码里做而不是写在 XAML 样式里</b>：XAML 样式读不到"上一页是谁"，
/// 而方向恰恰是"上一页 → 下一页"的函数。样式只能表达"变可见就淡入"这种与来路无关的动画。</para>
///
/// <para><b>为什么只有进场</b>：页面是叠在同一个容器里的兄弟节点，靠可见性切换，
/// 旧页立即消失，没有退场可做。</para>
///
/// <para><b>API 说明</b>：Avalonia 这边没有 WPF 的 <c>BeginAnimation</c>，
/// 动画是"构造一棵关键帧树 → <c>RunAsync</c> 跑一遍"。</para>
/// </summary>
public static class PageTransition
{
    private static readonly ConditionalWeakTable<Control, Runner> Runners = new();

    /// <summary>淡入时长。对齐 WPF 侧的 <c>Motion.Base</c>（160ms）。</summary>
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>滑入时长。对齐 WPF 侧的 <c>Motion.Slow</c>（240ms）。</summary>
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(240);

    /// <summary>位移距离（DIP）。</summary>
    public const double Distance = 16.0;

    /// <summary>
    /// 现在放不放动画。由设置页那一档决定（见 <c>App.axaml.cs</c> 的
    /// <c>ApplyMotionPreference</c>）。
    ///
    /// <para>Avalonia 这边没有系统级的"显示动画"开关可跟，所以"跟随系统"落到 true
    /// —— 判据在共享层，两端只有"系统那档是什么"不一样。</para>
    /// </summary>
    public static bool ShouldAnimate { get; set; } = true;

    /// <summary>给页面挂上入场过渡。</summary>
    public static void Attach(Control target)
    {
        Runners.GetValue(target, static item => new Runner(item));
    }

    private sealed class Runner
    {
        private readonly Control _target;
        private CancellationTokenSource? _cts;

        public Runner(Control target)
        {
            _target = target;
            target.PropertyChanged += OnTargetPropertyChanged;
        }

        private void OnTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            if (_target.IsVisible) Enter();
            else Cancel();
        }

        private void Enter()
        {
            if (!_target.IsVisible) return;
            if (!ShouldAnimate)
            {
                // 动画关着：直接落到位，不建动画对象。
                _target.Opacity = 1;
                _target.RenderTransform = null;
                return;
            }

            // 方向从 DataContext 上的 VM 读：那是唯一知道"上一页是谁"的地方。
            var direction = (_target.DataContext as MainWindowViewModel)?.LastPageDirection
                            ?? PageDirection.None;

            Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var offset = direction switch
            {
                // 正在往侧栏上方走 ⇒ 新页从上方进来。
                PageDirection.FromAbove => -Distance,
                PageDirection.FromBelow => Distance,
                // 首页只淡入：3D 皮肤模型会跟着飘，主脸不该晃。
                _ => 0.0,
            };

            var ease = new CubicEaseOut();

            var fade = new Animation
            {
                Duration = FadeDuration,
                Easing = ease,
                FillMode = FillMode.Forward,
            };
            fade.Children.Add(new KeyFrame
            {
                Cue = new Cue(0),
                Setters = { new Setter(Visual.OpacityProperty, 0.0) },
            });
            fade.Children.Add(new KeyFrame
            {
                Cue = new Cue(1),
                Setters = { new Setter(Visual.OpacityProperty, 1.0) },
            });

            _target.Opacity = 0;
            _ = fade.RunAsync(_target, token);

            if (offset != 0)
            {
                var slide = new Animation
                {
                    Duration = SlideDuration,
                    Easing = ease,
                    FillMode = FillMode.Forward,
                };
                slide.Children.Add(new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.RenderTransformProperty, new TranslateTransform(0, offset)) },
                });
                slide.Children.Add(new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.RenderTransformProperty, new TranslateTransform(0, 0)) },
                });
                _target.RenderTransform = new TranslateTransform(0, offset);
                _ = slide.RunAsync(_target, token).ContinueWith(_ =>
                {
                    // 滑完把变换收掉：留着一个"恒等"的 TranslateTransform 在树上，
                    // 下一轮换页时它会跟新动画的起点打架（两边都在写 RenderTransform）。
                    if (!token.IsCancellationRequested) _target.RenderTransform = null;
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        private void Cancel()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }
}
