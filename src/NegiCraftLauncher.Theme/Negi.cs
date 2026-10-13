using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NegiCraftLauncher.Theme;

/// <summary>
/// WPF 原生没有、但 Avalonia 侧有的几个属性，做成附加属性挂上去。
///
/// <para>这样做的目的是让两边 XAML 的写法尽量一致 —— 否则每处 <c>CornerRadius="8"</c>
/// 都得在 WPF 侧改成别的表达方式，P5 逐段搬 <c>MainWindow.axaml</c> 时到处都是特例。</para>
///
/// <para><see cref="CornerRadiusProperty"/> 与 <see cref="PlaceholderProperty"/> 是给控件模板读的
/// （<c>{Binding Path=(neg:Negi.X), RelativeSource={RelativeSource TemplatedParent}}</c>）。</para>
///
/// <para>下面那组 <c>bool</c> 状态位是 <c>Classes="NavBtn Active"</c> 的替代品：Avalonia 的样式类
/// 可以叠加（<c>Classes.Active="{Binding ...}"</c>），WPF 的 <c>Style</c> 不行 —— 一个元素只有一个
/// <c>Style</c>，条件外观只能靠 <c>Trigger</c>。所以把"类"变成附加属性，样式里写
/// <c>&lt;Trigger Property="neg:Negi.Active" Value="True"&gt;</c>，逐段搬 XAML 时几乎可以一对一。</para>
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

    /// <summary>
    /// 输入框左边的图标槽 —— 对应 Avalonia <c>TextBox.InnerLeftContent</c>（ShadUI 的搜索框就是用它放的放大镜）。
    ///
    /// <para>为什么要有这个东西：没有槽，图标只能"叠在输入框上面 + 给文字手调一个左内边距"，
    /// 于是图标位置和文字起始位是两条互不相干的数 —— 调一处就得重新量另一处，实例页那颗搜索框
    /// 就是这么来回改了三版 padding。槽在模板里排成一列，间距由布局给，padding 只管自己那 12。</para>
    /// </summary>
    public static readonly DependencyProperty LeadingIconProperty =
        DependencyProperty.RegisterAttached(
            "LeadingIcon",
            typeof(object),
            typeof(Negi),
            new FrameworkPropertyMetadata(null));

    public static object? GetLeadingIcon(DependencyObject element) => element.GetValue(LeadingIconProperty);

    public static void SetLeadingIcon(DependencyObject element, object? value) =>
        element.SetValue(LeadingIconProperty, value);

    /// <summary>对应 Avalonia 的 <c>Classes="... Active"</c>（导航按钮的选中态）。</summary>
    public static readonly DependencyProperty ActiveProperty = RegisterState("Active");

    public static bool GetActive(DependencyObject element) => (bool)element.GetValue(ActiveProperty);

    public static void SetActive(DependencyObject element, bool value) => element.SetValue(ActiveProperty, value);

    /// <summary>对应 Avalonia 的 <c>Classes="... On"</c>（分段按钮 / 设置导航的选中态）。</summary>
    public static readonly DependencyProperty OnProperty = RegisterState("On");

    public static bool GetOn(DependencyObject element) => (bool)element.GetValue(OnProperty);

    public static void SetOn(DependencyObject element, bool value) => element.SetValue(OnProperty, value);

    /// <summary>对应 Avalonia 的 <c>Classes="... Current"</c>（实例卡片是当前实例）。</summary>
    public static readonly DependencyProperty CurrentProperty = RegisterState("Current");

    public static bool GetCurrent(DependencyObject element) => (bool)element.GetValue(CurrentProperty);

    public static void SetCurrent(DependencyObject element, bool value) => element.SetValue(CurrentProperty, value);

    /// <summary>对应 Avalonia 的 <c>Classes="... Danger"</c>（危险操作的图标按钮）。</summary>
    public static readonly DependencyProperty DangerProperty = RegisterState("Danger");

    public static bool GetDanger(DependencyObject element) => (bool)element.GetValue(DangerProperty);

    public static void SetDanger(DependencyObject element, bool value) => element.SetValue(DangerProperty, value);

    /// <summary>清空文本框内容的路由命令（配合搜索框清空按钮使用）。</summary>
    public static readonly RoutedUICommand ClearTextCommand =
        new("ClearText", "ClearText", typeof(Negi));

    static Negi()
    {
        CommandManager.RegisterClassCommandBinding(
            typeof(System.Windows.Controls.TextBox),
            new CommandBinding(ClearTextCommand, OnClearTextExecuted, OnClearTextCanExecute));
    }

    private static void OnClearTextCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            e.CanExecute = !string.IsNullOrEmpty(tb.Text);
            e.Handled = true;
        }
    }

    private static void OnClearTextExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb)
        {
            tb.Text = string.Empty;
            tb.Focus();
            e.Handled = true;
        }
    }

    private static DependencyProperty RegisterState(string name) =>
        DependencyProperty.RegisterAttached(
            name,
            typeof(bool),
            typeof(Negi),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
}

