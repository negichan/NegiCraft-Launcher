using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NegiCraftLauncher.App.Wpf.Controls;

/// <summary>
/// WPF 版的 <c>Views/Pop.cs</c>：弹层从锚点那一角缩放着长出来，同时淡入。
///
/// <para><b>与 Avalonia 版的差别（有意为之）</b>：那边是手写的时间线，从触发控件的矩形
/// 出发、冲过头、回弹再停住（三段式）。WPF 这边用 <see cref="Storyboard"/> +
/// <see cref="BackEase"/> 做同样的观感，但只按锚点定缩放原点，不做"从触发控件矩形起飞"的位移 ——
/// 那需要拿到触发控件的实时布局矩形，WPF 里要等 <c>LayoutUpdated</c>，收益不值这份复杂度。
/// 静止状态（Opacity=1、无变换）两边完全一致，像素级对照不受影响。</para>
///
/// <para>默认样式把弹层设成 <c>Opacity=0</c> + <c>IsHitTestVisible=False</c>，
/// 由这里在打开/关闭时接管，所以关着的弹层既不显示也不吃点击，但仍在布局树里
/// （不占位就没有尺寸，动画就没法量）。</para>
/// </summary>
public static class Pop
{
    /// <summary>绑定到 VM 的 <c>IsXxxOpen</c>，驱动开合。</summary>
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.RegisterAttached(
            "IsOpen",
            typeof(bool),
            typeof(Pop),
            new PropertyMetadata(false, OnIsOpenChanged));

    public static bool GetIsOpen(DependencyObject element) => (bool)element.GetValue(IsOpenProperty);

    public static void SetIsOpen(DependencyObject element, bool value) => element.SetValue(IsOpenProperty, value);

    /// <summary>缩放原点所在的那一角：BottomLeft / BottomCenter / BottomRight / TopLeft / TopCenter / TopRight / Center。</summary>
    public static readonly DependencyProperty AnchorProperty =
        DependencyProperty.RegisterAttached(
            "Anchor",
            typeof(string),
            typeof(Pop),
            new PropertyMetadata("BottomLeft"));

    public static string GetAnchor(DependencyObject element) => (string)element.GetValue(AnchorProperty);

    public static void SetAnchor(DependencyObject element, string value) => element.SetValue(AnchorProperty, value);

    private const double OpenMs = 160;
    private const double CloseMs = 130;
    private const double StartScale = 0.92;
    private const double CloseScale = 0.96;

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if (e.NewValue is true)
        {
            Open(element);
        }
        else
        {
            Close(element);
        }
    }

    private static void Open(FrameworkElement element)
    {
        var (x, y) = AnchorFraction(GetAnchor(element));
        element.RenderTransformOrigin = new Point(x, y);

        var scale = new ScaleTransform(StartScale, StartScale);
        element.RenderTransform = scale;

        // 飞出来的时候会盖在指针下面，这期间不能吃点击，否则底下的行会先亮一下再被盖住。
        element.IsHitTestVisible = true;

        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var storyboard = new Storyboard();

        storyboard.Children.Add(Animation(element, UIElement.OpacityProperty, 0, 1, OpenMs, null));
        storyboard.Children.Add(Animation(scale, ScaleTransform.ScaleXProperty, StartScale, 1, OpenMs, ease));
        storyboard.Children.Add(Animation(scale, ScaleTransform.ScaleYProperty, StartScale, 1, OpenMs, ease));

        storyboard.Begin(element, true);
    }

    private static void Close(FrameworkElement element)
    {
        var current = element.RenderTransform as ScaleTransform;
        if (current is null)
        {
            var (x, y) = AnchorFraction(GetAnchor(element));
            element.RenderTransformOrigin = new Point(x, y);
            current = new ScaleTransform(1, 1);
            element.RenderTransform = current;
        }

        element.IsHitTestVisible = false;

        var storyboard = new Storyboard();
        storyboard.Children.Add(Animation(element, UIElement.OpacityProperty, null, 0, CloseMs, null));
        storyboard.Children.Add(Animation(current, ScaleTransform.ScaleXProperty, null, CloseScale, CloseMs, null));
        storyboard.Children.Add(Animation(current, ScaleTransform.ScaleYProperty, null, CloseScale, CloseMs, null));

        storyboard.Begin(element, true);
    }

    private static DoubleAnimation Animation(
        DependencyObject target,
        DependencyProperty property,
        double? from,
        double to,
        double milliseconds,
        IEasingFunction? ease)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };

        if (from.HasValue) animation.From = from.Value;

        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }

    private static (double X, double Y) AnchorFraction(string anchor) => anchor switch
    {
        "BottomRight" => (1, 1),
        "BottomCenter" => (0.5, 1),
        "TopRight" => (1, 0),
        "TopLeft" => (0, 0),
        "TopCenter" => (0.5, 0),
        "Center" => (0.5, 0.5),
        _ => (0, 1),
    };
}
