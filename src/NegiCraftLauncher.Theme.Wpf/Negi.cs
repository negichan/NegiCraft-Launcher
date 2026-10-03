using System.Windows;
using System.Windows.Media;

namespace NegiCraftLauncher.Theme.Wpf;

/// <summary>
/// WPF 原生没有、但 Avalonia 侧有的几个属性，做成附加属性挂上去。
///
/// <para>这样做的目的是让两边 XAML 的写法尽量一致 —— 否则每处 <c>CornerRadius="8"</c>
/// 都得在 WPF 侧改成别的表达方式，P5 逐段搬 <c>MainWindow.axaml</c> 时到处都是特例。</para>
///
/// <para>两个都是给控件模板读的（<c>{Binding Path=(neg:Negi.X), RelativeSource={RelativeSource TemplatedParent}}</c>），
/// 不是拿来在业务代码里用的。</para>
/// </summary>
public static class Negi
{
    /// <summary>圆角。WPF 的 <c>Button</c> / <c>TextBox</c> / <c>ProgressBar</c> 都没有这个属性。</summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.RegisterAttached(
            "CornerRadius",
            typeof(CornerRadius),
            typeof(Negi),
            new FrameworkPropertyMetadata(new CornerRadius(0), FrameworkPropertyMetadataOptions.AffectsRender));

    public static CornerRadius GetCornerRadius(DependencyObject element) =>
        (CornerRadius)element.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject element, CornerRadius value) =>
        element.SetValue(CornerRadiusProperty, value);

    /// <summary>输入框的水印文字（Avalonia 的 <c>TextBox.Watermark</c> / <c>PlaceholderText</c>）。</summary>
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(string),
            typeof(Negi),
            new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) =>
        (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) =>
        element.SetValue(PlaceholderProperty, value);
}
