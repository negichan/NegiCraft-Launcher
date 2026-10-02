using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace NegiCraftLauncher.App.Views;

/// <summary>
///     Grows a card out of the control that opened it: the card starts exactly on the trigger's
///     rectangle, expands into place slightly past its size, dips back under it and rests.
///     Attached to a popover with <see cref="TriggerProperty" /> and <see cref="IsOpenProperty" />.
/// </summary>
public class Pop
{
    private static readonly ConditionalWeakTable<Control, Runner> Runners = new();

    static Pop()
    {
        IsOpenProperty.Changed.AddClassHandler<Control>(OnIsOpenChanged);
    }

    /// <summary>
    ///     The control whose rectangle the card grows out of. Without it the card simply unfolds
    ///     from a smaller version of itself.
    /// </summary>
    public static readonly AttachedProperty<Control?> TriggerProperty =
        AvaloniaProperty.RegisterAttached<Pop, Control, Control?>("Trigger", null, false, BindingMode.OneTime);

    /// <summary>
    ///     The corner shared by card and trigger: it stays pinned while the card travels out.
    /// </summary>
    public static readonly AttachedProperty<string> AnchorProperty =
        AvaloniaProperty.RegisterAttached<Pop, Control, string>("Anchor", "BottomLeft", false, BindingMode.OneTime);

    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Pop, Control, bool>("IsOpen");

    public static Control? GetTrigger(Control control) => control.GetValue(TriggerProperty);

    public static void SetTrigger(Control control, Control? value) => control.SetValue(TriggerProperty, value);

    public static string GetAnchor(Control control) => control.GetValue(AnchorProperty);

    public static void SetAnchor(Control control, string value) => control.SetValue(AnchorProperty, value);

    public static bool GetIsOpen(Control control) => control.GetValue(IsOpenProperty);

    public static void SetIsOpen(Control control, bool value) => control.SetValue(IsOpenProperty, value);

    private static void OnIsOpenChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        var runner = Runners.GetValue(control, static c => new Runner(c));
        if (args.NewValue is true)
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
        // One gesture: the expansion decelerates straight into the overshoot, then the card dips
        // under its size and rests. Every leg needs ~100ms to read as motion; shorter legs read
        // as a shake. The travel itself stays short so the departure feels crisp.
        private const double TravelMs = 150;
        private const double DipMs = 110;
        private const double RestMs = 110;
        private const double TotalMs = TravelMs + DipMs + RestMs;
        private const double CloseMs = 160;

        private const double Peak = 1.05;
        private const double Dip = 0.987;

        // Used when there is no trigger to grow out of.
        private const double FallbackScale = 0.8;

        // Box shadows live outside the card's bounds; the reveal clip must not cut them off the
        // part of the card that is already visible.
        private const double ShadowMargin = 64;

        private readonly Control _target;
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _clock = new();

        private double _popW, _popH;
        private double _scale0 = FallbackScale;
        private double _offsetX, _offsetY;
        private double _reveal0;   // visible height, in screen units, at the start of the travel
        private double _anchorY = 1;

        // Position on the open timeline, in ms: interrupts resume from wherever the card is.
        private double _p;
        private double _from;
        private double _spanMs;
        private bool _closing;

        public Runner(Control target)
        {
            _target = target;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(6) };
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

            // While the card is in flight it sits under the cursor; hit testing it now would let
            // the row under the pointer light up with its hover style and flicker as the card
            // grows. It becomes interactive once it settles.
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

            // The retract skips the settle: it plays the expansion back from plain full size.
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
                _target.ClearValue(InputElement.IsHitTestVisibleProperty);
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

        // The card riding its pinned corner out of the trigger. Avalonia applies
        // RenderTransformOrigin as translate(origin) * transform * translate(-origin), so the
        // translation below moves that corner in unscaled units. The travel ends at `top`:
        // the overshoot while opening, plain full size while retracting.
        private void ApplyTravel(double u, double top)
        {
            _p = u * TravelMs;
            var e = 1 - Math.Pow(1 - u, 2);
            var scale = Lerp(_scale0, top, e);
            var local = Lerp(_reveal0 / _scale0, _popH, e);

            _target.RenderTransform = new MatrixTransform(new Matrix(scale, 0, 0, scale, _offsetX * (1 - e), _offsetY * (1 - e)));
            _target.Opacity = 1;
            _target.Clip = ClipFor(local);
        }

        private void Rest()
        {
            _p = 0;
            _target.RenderTransform = null;
            _target.Clip = null;
            _target.ClearValue(Visual.OpacityProperty);
            _target.ClearValue(InputElement.IsHitTestVisibleProperty);
        }

        private Geometry? ClipFor(double localHeight)
        {
            if (localHeight >= _popH - 0.5)
            {
                return null;
            }

            // The reveal edge grows its shadow margin in over the last stretch, so the top shadow
            // does not pop when the clip is dropped.
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
            _target.RenderTransformOrigin = new RelativePoint(fx, fy, RelativeUnit.Relative);

            var pop = _target.Bounds;
            if (pop.Width < 1 || pop.Height < 1)
            {
                return false;
            }

            _popW = pop.Width;
            _popH = pop.Height;

            var trigger = GetTrigger(_target);
            if (_target.Parent is not Visual host || trigger is not { IsVisible: true })
            {
                return false;
            }

            if (trigger.TransformToVisual(host) is not Matrix to)
            {
                return false;
            }

            var tw = trigger.Bounds.Width;
            var th = trigger.Bounds.Height;
            if (tw < 1 || th < 1)
            {
                return false;
            }

            var triggerAnchor = to.Transform(new Point(fx * tw, fy * th));
            var popAnchor = new Point(pop.X + fx * pop.Width, pop.Y + fy * pop.Height);

            _scale0 = Math.Clamp(tw / pop.Width, 0.2, 1);
            _offsetX = triggerAnchor.X - popAnchor.X;
            _offsetY = triggerAnchor.Y - popAnchor.Y;
            _reveal0 = Math.Min(th, pop.Height);
            return true;
        }

        private static (double X, double Y) AnchorFraction(string anchor) => anchor switch
        {
            "BottomRight" => (1, 1),
            "BottomCenter" => (0.5, 1),
            "TopRight" => (1, 0),
            "TopCenter" => (0.5, 0),
            "Center" => (0.5, 0.5),
            _ => (0, 1)
        };

        private static double Smooth(double from, double to, double u) => from + (to - from) * (u * u * (3 - 2 * u));

        private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    }
}
