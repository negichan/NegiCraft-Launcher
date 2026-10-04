using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NegiCraftLauncher.App.Controls;

/// <summary>
/// WPF 版的 <c>Views/Pop.cs</c> —— 同一套时间线，参数逐字对照。
///
/// <para>卡片从 <see cref="TriggerProperty"/> 指的控件矩形里长出来：按缓出（1-(1-u)²）飞
/// <c>150ms</c> 冲到自身尺寸的 <c>1.05</c>，再用 smoothstep 落回 <c>0.987</c>（<c>110ms</c>），
/// 最后回到 <c>1</c> 停住（<c>110ms</c>）。关闭是 <c>160ms</c> 的反向收拢，不播放"落定"那两段。
/// 飞行期间还有一条揭示裁切：只露出从锚点那侧起、原本和触发控件一样高的部分，
/// 随行程长到整张卡片，所以观感是"从按钮里被拉出来"而不是"原地放大"。</para>
///
/// <para><b>与 Avalonia 版的唯一差别</b>：那边靠 <c>Classes.Open</c> 在打开期间把
/// <c>IsHitTestVisible</c> 顶回 True；WPF 的样式没有类选择器，所以这里在行程结束时
/// 显式写 True、回到静止时显式写 False（<see cref="PopoverStyle"/> 的默认值是 False）。
/// 静止状态两边仍然完全一致 —— Opacity=0、无变换、无裁切 —— 像素级对照不受影响。</para>
///
/// <para>默认样式把弹层设成 <c>Opacity=0</c> + <c>IsHitTestVisible=False</c>，
/// 由这里在打开/关闭时接管，所以关着的弹层既不显示也不吃点击，但仍在布局树里
/// （不占位就没有尺寸，动画就没法量）。</para>
/// </summary>
public static class Pop
{
    // 一个弹层一个 Runner；弱表保证弹层被回收时 Runner 跟着走。
    private static readonly ConditionalWeakTable<FrameworkElement, Runner> Runners = new();

    /// <summary>绑定到 VM 的 <c>IsXxxOpen</c>，驱动开合。</summary>
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.RegisterAttached(
            "IsOpen",
            typeof(bool),
            typeof(Pop),
            new PropertyMetadata(false, OnIsOpenChanged));

    public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);

    public static void SetIsOpen(DependencyObject element, bool value) => element.SetValue(IsOpenProperty, value);

    /// <summary>卡片从哪个控件的矩形里长出来。不写就只是"从一个缩小的自己展开"。</summary>
    public static readonly DependencyProperty TriggerProperty =
        DependencyProperty.RegisterAttached(
            "Trigger",
            typeof(FrameworkElement),
            typeof(Pop),
            new PropertyMetadata(null));

    public static FrameworkElement? GetTrigger(DependencyObject element) => (FrameworkElement?)element.GetValue(TriggerProperty);

    public static void SetTrigger(DependencyObject element, FrameworkElement? value) => element.SetValue(TriggerProperty, value);

    /// <summary>缩放原点所在的那一角：BottomLeft / BottomCenter / BottomRight / TopCenter / TopRight / Center。</summary>
    public static readonly DependencyProperty AnchorProperty =
        DependencyProperty.RegisterAttached(
            "Anchor",
            typeof(string),
            typeof(Pop),
            new PropertyMetadata("BottomLeft"));

    public static string GetAnchor(DependencyObject element) => (string)element.GetValue(AnchorProperty);

    public static void SetAnchor(DependencyObject element, string value) => element.SetValue(AnchorProperty, value);

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        var runner = Runners.GetValue(element, static c => new Runner(c));
        if (e.NewValue is true)
        {
            runner.Open();
        }
        else
        {
            runner.Close();
        }
    }

    private sealed class Runner
    {
        // 一次手势：加速飞出直接接上过冲，再落回自己尺寸以下停住。
        // 每一段都要 ~100ms 才读得出"在动"；更短会像抖了一下。行程本身保持短，起步才脆。
        private const double TravelMs = 150;
        private const double DipMs = 110;
        private const double RestMs = 110;
        private const double TotalMs = TravelMs + DipMs + RestMs;
        private const double CloseMs = 160;

        private const double Peak = 1.05;
        private const double Dip = 0.987;

        // 没有触发控件可用时的起点缩放。
        private const double FallbackScale = 0.8;

        // 投影画在卡片边界之外；揭示裁切不能把它切掉。
        private const double ShadowMargin = 64;

        private readonly FrameworkElement _target;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();

        private double _popW, _popH;
        private double _scale0 = FallbackScale;
        private double _offsetX, _offsetY;
        private double _reveal0;   // 行程开始时可见的高度（屏幕单位）
        private double _anchorY = 1;

        // 打开时间线上的位置（ms）：被打断时从卡片当前所在处接着走。
        private double _p;
        private double _from;
        private double _spanMs;
        private bool _closing;

        public Runner(FrameworkElement target)
        {
            _target = target;
            // 6ms 只是"尽快"；真实节奏由 Stopwatch 决定，所以抖动只影响平滑度，不影响时长。
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(6) };
            _timer.Tick += (_, _) => Step();
        }

        public void Open()
        {
            if (!Measure())
            {
                _scale0 = FallbackScale;
                _offsetX = _offsetY = 0;
                _reveal0 = _popH;
            }

            // 卡片在飞的时候正压在指针底下；这期间吃点击的话，底下的行会先亮一下再被盖住。
            // 落定之后才变成可交互的。
            _target.IsHitTestVisible = false;

            _closing = false;
            _from = _p;
            _spanMs = Math.Max(100, TotalMs - _from);
            _clock.Restart();
            _timer.Stop();
            _timer.Start();
            ApplyTimeline(_from);
        }

        public void Close()
        {
            _closing = true;
            _target.IsHitTestVisible = false;

            // 收拢跳过"落定"那两段：直接从整尺寸把行程倒放回去。
            _from = Math.Min(_p, TravelMs);
            if (_from <= 0)
            {
                Rest();
                return;
            }

            _spanMs = Math.Max(80, CloseMs * _from / TravelMs);
            _clock.Restart();
            _timer.Stop();
            _timer.Start();
            ApplyTravel(_from / TravelMs, 1.0);
        }

        private void Step()
        {
            var ms = _clock.Elapsed.TotalMilliseconds;
            if (_closing)
            {
                var k = Math.Min(1, ms / _spanMs);
                if (k >= 1)
                {
                    _timer.Stop();
                    Rest();
                    return;
                }

                ApplyTravel(_from * (1 - k) / TravelMs, 1.0);
                return;
            }

            var done = Math.Min(1, ms / _spanMs);
            if (done >= 1)
            {
                _timer.Stop();
                _p = TotalMs;
                _target.RenderTransform = null;
                _target.Clip = null;
                // 这里 Avalonia 是 ClearValue 回落到 .Open 类；WPF 没有类选择器，只能显式写。
                _target.IsHitTestVisible = true;
                return;
            }

            ApplyTimeline(_from + (TotalMs - _from) * done);
        }

        private void ApplyTimeline(double p)
        {
            _p = p;
            if (p <= TravelMs)
            {
                ApplyTravel(p / TravelMs, Peak);
                return;
            }

            const double dipEnds = DipMs / (TotalMs - TravelMs);
            var q = (p - TravelMs) / (TotalMs - TravelMs);
            var scale = q <= dipEnds
                ? Smooth(Peak, Dip, q / dipEnds)
                : Smooth(Dip, 1, (q - dipEnds) / (1 - dipEnds));

            _target.RenderTransform = new MatrixTransform(new Matrix(scale, 0, 0, scale, 0, 0));
            _target.Opacity = 1;
            _target.Clip = null;
        }

        // 卡片钉着与触发控件共用的那一角飞出来。WPF 与 Avalonia 一样把 RenderTransformOrigin
        // 当成 translate(origin) * transform * translate(-origin)，所以下面这个平移
        // 走的是**未缩放**的单位。行程终点是 `top`：打开时是过冲值，收拢时就是整尺寸。
        private void ApplyTravel(double u, double top)
        {
            _p = u * TravelMs;
            var e = 1 - Math.Pow(1 - u, 2);
            var scale = Lerp(_scale0, top, e);
            var local = Lerp(_reveal0 / _scale0, _popH, e);

            _target.RenderTransform = new MatrixTransform(
                new Matrix(scale, 0, 0, scale, _offsetX * (1 - e), _offsetY * (1 - e)));
            _target.Opacity = 1;
            _target.Clip = ClipFor(local);
        }

        private void Rest()
        {
            _p = 0;
            _target.RenderTransform = null;
            _target.Clip = null;
            // Opacity 交回样式（PopoverStyle 的 0），弹层就"回到关着"的样子。
            _target.ClearValue(UIElement.OpacityProperty);
            _target.IsHitTestVisible = false;
        }

        private Geometry? ClipFor(double localHeight)
        {
            if (localHeight >= _popH - 0.5)
            {
                return null;
            }

            // 揭示边在最后一段把投影余量一起长进来，这样裁切被撤掉时顶部投影不会"啪"地出现。
            var ramp = Math.Clamp((localHeight - 0.8 * _popH) / (0.2 * _popH), 0, 1);
            const double m = ShadowMargin;
            double top, bottom;
            if (_anchorY >= 0.5)
            {
                top = _popH - localHeight - m * ramp;
                bottom = _popH + m;
            }
            else
            {
                top = -m;
                bottom = localHeight + m * ramp;
            }

            return new RectangleGeometry(new Rect(-m, top, _popW + 2 * m, bottom - top));
        }

        private bool Measure()
        {
            var (fx, fy) = AnchorFraction(GetAnchor(_target));
            _anchorY = fy;
            _target.RenderTransformOrigin = new Point(fx, fy);

            var w = _target.ActualWidth;
            var h = _target.ActualHeight;
            if (w < 1 || h < 1)
            {
                return false;
            }

            _popW = w;
            _popH = h;

            var trigger = GetTrigger(_target);
            if (_target.Parent is not FrameworkElement host || trigger is null || !trigger.IsVisible)
            {
                return false;
            }

            var tw = trigger.ActualWidth;
            var th = trigger.ActualHeight;
            if (tw < 1 || th < 1)
            {
                return false;
            }

            // 量的是**布局位置**：留着上一轮的 RenderTransform 会把锚点算歪。
            var saved = _target.RenderTransform;
            _target.RenderTransform = null;
            Point triggerAnchor, popAnchor;
            try
            {
                triggerAnchor = trigger.TransformToVisual(host).Transform(new Point(fx * tw, fy * th));
                popAnchor = _target.TransformToVisual(host).Transform(new Point(fx * w, fy * h));
            }
            catch (InvalidOperationException)
            {
                // 触发控件和弹层不在同一棵可视树里（比如弹层被挪进了另一个 Panel）。
                _target.RenderTransform = saved;
                return false;
            }

            _target.RenderTransform = saved;

            _scale0 = Math.Clamp(tw / _popW, 0.2, 1);
            _offsetX = triggerAnchor.X - popAnchor.X;
            _offsetY = triggerAnchor.Y - popAnchor.Y;
            _reveal0 = Math.Min(th, _popH);
            return true;
        }

        // ⚠️ 这张表要与 Avalonia 版**逐字一致**：那边没有 "TopLeft" 分支，
        // 写 Anchor="TopLeft" 会落到 `_` 变成 BottomLeft。这里也不补 —— 补了就出现
        // "同一份 XAML 两个平台表现不同"的隐性分歧。要支持得两边一起加。
        private static (double X, double Y) AnchorFraction(string anchor) => anchor switch
        {
            "BottomRight" => (1, 1),
            "BottomCenter" => (0.5, 1),
            "TopRight" => (1, 0),
            "TopCenter" => (0.5, 0),
            "Center" => (0.5, 0.5),
            _ => (0, 1),
        };

        private static double Smooth(double from, double to, double u) => from + (to - from) * (u * u * (3 - 2 * u));

        private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    }
}
