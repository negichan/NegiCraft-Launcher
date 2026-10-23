using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace NegiCraftLauncher.App.Avalonia.Views;

/// <summary>
///     Plays a Popup's entrance with our own timing instead of letting it appear instantly.
///
///     <para>
///     Avalonia's <see cref="Popup" /> has no built-in entrance animation at all (WPF has
///     <c>PopupAnimation</c>, which we deliberately do not use on the other side because it
///     bypasses the motion scheduling). So the pop layer would just pop into existence here.
///     This attached behaviour gives both shells the same gesture: 0.94 → 1.014 → 1 scale
///     plus a short fade, anchored at the corner nearest the button it hangs off.
///     </para>
///
///     <para>
///     The switch is <see cref="PageTransition.ShouldAnimate" /> — the Avalonia shell's single
///     answer to "界面动画 is on", set from the three-way setting in <c>App.axaml.cs</c>.
///     With it off the popup lands at full size immediately.
///     </para>
/// </summary>
public static class PopupMotion
{
    private static readonly ConditionalWeakTable<Popup, Runner> Runners = new();

    public static readonly AttachedProperty<bool> EnableProperty =
        AvaloniaProperty.RegisterAttached<Popup, Popup, bool>("Enable");

    /// <summary><c>TopRight</c> (default) / <c>TopLeft</c> / <c>BottomRight</c> / <c>BottomLeft</c> / <c>Center</c>.</summary>
    public static readonly AttachedProperty<string> OriginProperty =
        AvaloniaProperty.RegisterAttached<Popup, Popup, string>("Origin", "TopRight");

    static PopupMotion()
    {
        EnableProperty.Changed.AddClassHandler<Popup>(OnEnableChanged);
    }

    public static bool GetEnable(Popup popup) => popup.GetValue(EnableProperty);

    public static void SetEnable(Popup popup, bool value) => popup.SetValue(EnableProperty, value);

    public static string GetOrigin(Popup popup) => popup.GetValue(OriginProperty);

    public static void SetOrigin(Popup popup, string value) => popup.SetValue(OriginProperty, value);

    private static void OnEnableChanged(Popup popup, AvaloniaPropertyChangedEventArgs args)
    {
        // Subscribe once: the attached property may be assigned again by a re-templated parent.
        Runners.GetValue(popup, static item => new Runner(item));
    }

    private static RelativePoint OriginAt(string origin) => origin switch
    {
        "TopLeft" => new RelativePoint(0, 0, RelativeUnit.Relative),
        "BottomRight" => new RelativePoint(1, 1, RelativeUnit.Relative),
        "BottomLeft" => new RelativePoint(0, 1, RelativeUnit.Relative),
        "Center" => new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
        _ => new RelativePoint(1, 0, RelativeUnit.Relative),
    };

    /// <summary>
    ///     上一次开场做了什么（<c>animated</c> / <c>skip:…</c> / <c>never</c>）。
    ///     只在调试桥上读 —— 静态截图分辨不出"没播"和"已经播完"，得有个话说。
    /// </summary>
    internal static string LastPlay { get; private set; } = "never";

    private sealed class Runner
    {
        // Same numbers as the WPF side (Motion.Base → overshoot, Motion.Slow → rest); that side
        // reads them from its token table, this one has no shared table to read from.
        private const double TravelMs = 160;
        private const double RestMs = 240;
        private const double FadeMs = 100;

        private const double RestScale = 0.94;
        private const double Overshoot = 1.014;

        private readonly Popup _popup;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(6) };
        private readonly Stopwatch _clock = new();

        public Runner(Popup popup)
        {
            _popup = popup;
            _popup.Opened += (_, _) => Play();
            _popup.Closed += (_, _) => Rest();
            _timer.Tick += (_, _) => Step();
        }

        private Control? Child => _popup.Child;

        private void Play()
        {
            if (Child is not { } child)
            {
                LastPlay = "skip:no-child";
                return;
            }

            child.RenderTransformOrigin = OriginAt(GetOrigin(_popup));
            if (!PageTransition.ShouldAnimate)
            {
                LastPlay = "skip:animate=false";
                Settle(child);
                return;
            }

            // 首帧就必须是「缩小 + 透明」，所以初始状态在这里落，不看 IsLoaded。
            // Avalonia 的 Popup.Opened 触发时 Child 常常还没 Loaded（实测 loaded=False），
            // 那时候直接起时钟会白跑；改成先落状态、等 Loaded 再计时 —— Loaded 也在首帧渲染之前。
            child.RenderTransform = new ScaleTransform(RestScale, RestScale);
            child.Opacity = 0;

            if (child.IsLoaded)
            {
                LastPlay = "animated";
                StartTicker();
                return;
            }

            LastPlay = "animated:await-loaded";
            child.Loaded += OnLoaded;

            void OnLoaded(object? sender, RoutedEventArgs e)
            {
                child.Loaded -= OnLoaded;
                StartTicker();
            }
        }

        private void StartTicker()
        {
            _clock.Restart();
            _timer.Stop();
            _timer.Start();
        }

        private void Step()
        {
            if (Child is not { } child)
            {
                _timer.Stop();
                return;
            }

            var ms = _clock.Elapsed.TotalMilliseconds;
            if (ms >= RestMs)
            {
                _timer.Stop();
                Settle(child);
                return;
            }

            var scale = ms <= TravelMs
                ? Lerp(RestScale, Overshoot, EaseOut(ms / TravelMs))
                : Lerp(Overshoot, 1, EaseOut((ms - TravelMs) / (RestMs - TravelMs)));

            child.RenderTransform = new ScaleTransform(scale, scale);
            child.Opacity = Math.Min(1, EaseOut(ms / FadeMs));
        }

        /// <summary>Land on the final state. Idempotent — safe to call after the timer stopped.</summary>
        private static void Settle(Control child)
        {
            child.RenderTransform = null;
            child.Opacity = 1;
        }

        /// <summary>Popup is gone: wipe the state so the next open does not start from a leftover.</summary>
        private void Rest()
        {
            _timer.Stop();
            if (Child is { } child) Settle(child);
        }

        private static double EaseOut(double k)
        {
            var t = Math.Clamp(k, 0, 1);
            return 1 - (1 - t) * (1 - t);
        }

        private static double Lerp(double from, double to, double k) => from + (to - from) * k;
    }
}
