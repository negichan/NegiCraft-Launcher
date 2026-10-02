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
///     rectangle and expands into place without squashing its content.
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
        private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan CloseTime = TimeSpan.FromMilliseconds(200);

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

        // Where the travel is right now, and the run in flight: closing replays the same curve
        // backwards from whatever progress was reached, so interrupts never jump.
        private double _t;
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
            _from = _t;
            _spanMs = Math.Max(80, Duration.TotalMilliseconds * (1 - _from));
            _clock.Restart();
            _timer.Stop();
            _timer.Start();
            Apply(_from);
        }

        public void Close()
        {
            _closing = true;
            _from = _t;
            _target.IsHitTestVisible = false;
            if (_from <= 0)
            {
                Rest();
                return;
            }

            _spanMs = Math.Max(60, CloseTime.TotalMilliseconds * _from);
            _clock.Restart();
            _timer.Stop();
            _timer.Start();
            Apply(_from);
        }

        private void Step()
        {
            var u = Math.Min(1, _clock.Elapsed.TotalMilliseconds / _spanMs);
            if (u >= 1)
            {
                _timer.Stop();
                if (_closing)
                {
                    Rest();
                }
                else
                {
                    _t = 1;
                    _target.RenderTransform = null;
                    _target.Clip = null;
                    _target.ClearValue(InputElement.IsHitTestVisibleProperty);
                }

                return;
            }

            Apply(_closing ? _from * (1 - u) : _from + (1 - _from) * u);
        }

        // Avalonia applies RenderTransformOrigin as translate(origin) * transform * translate(-origin),
        // so the translation below moves the pinned corner in unscaled units.
        private void Apply(double t)
        {
            var e = 1 - Math.Pow(1 - t, 3);
            var scale = Lerp(_scale0, 1, e);
            var visible = Lerp(_reveal0, _popH, e);

            _target.RenderTransform = new MatrixTransform(new Matrix(scale, 0, 0, scale, _offsetX * (1 - e), _offsetY * (1 - e)));
            _target.Opacity = 1;
            _target.Clip = ClipFor(visible / scale);
            _t = t;
        }

        private void Rest()
        {
            _t = 0;
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

        private static double Lerp(double from, double to, double t) => from + (to - from) * t;
    }
}
