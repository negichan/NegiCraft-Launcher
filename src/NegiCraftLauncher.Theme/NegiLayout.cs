using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NegiCraftLauncher.Theme;

/// <summary>
/// <see cref="Grid"/> + 一行写法的行列定义：<c>Columns="160 24 *"</c> / <c>Rows="Auto *"</c>。
///
/// <para>WPF 的 <c>Grid</c> 只认 <c>&lt;Grid.ColumnDefinitions&gt;</c> 那一坨冗长的子元素，
/// 而 Avalonia 侧满屏都是 <c>ColumnDefinitions="Auto * Auto"</c>。P5 逐段搬 1586 行 XAML 时，
/// 这层适配能把每处 6 行的样板压回 1 行，也让两边的 diff 能对齐着看。</para>
///
/// <para>分隔符空格或逗号都行；<c>*</c> 是 1 份星号，<c>2*</c> 是 2 份，<c>Auto</c> 是自动，
/// 纯数字是固定像素 —— 与 Avalonia 的语义一致。</para>
/// </summary>
public class NegiGrid : Grid
{
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(
            nameof(Columns),
            typeof(string),
            typeof(NegiGrid),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnColumnsChanged));

    public string? Columns
    {
        get => (string?)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty RowsProperty =
        DependencyProperty.Register(
            nameof(Rows),
            typeof(string),
            typeof(NegiGrid),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnRowsChanged));

    public string? Rows
    {
        get => (string?)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    private static void OnColumnsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (NegiGrid)d;
        grid.ColumnDefinitions.Clear();
        foreach (var length in Parse((string?)e.NewValue))
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = length });
        }
    }

    private static void OnRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var grid = (NegiGrid)d;
        grid.RowDefinitions.Clear();
        foreach (var length in Parse((string?)e.NewValue))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = length });
        }
    }

    private static IEnumerable<GridLength> Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) yield break;

        foreach (var raw in spec.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                yield return GridLength.Auto;
                continue;
            }

            if (raw.EndsWith('*'))
            {
                var head = raw[..^1];
                var weight = head.Length == 0
                    ? 1d
                    : double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : 1d;
                yield return new GridLength(weight, GridUnitType.Star);
                continue;
            }

            yield return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)
                ? new GridLength(px)
                : GridLength.Auto;
        }
    }
}

/// <summary>
/// <see cref="StackPanel"/> + <c>Spacing</c>：WPF 的 <c>StackPanel</c> 没有这个属性
/// （那是 Avalonia / WinUI 的），只能给每个子元素挂 Margin，一屏下来全是噪音。
///
/// <para>自己实现 <c>Measure/Arrange</c> 而不是给子元素塞 Margin —— 子元素自己可能已经有
/// Margin（例如 <c>Margin="0,0,12,12"</c> 的实例卡片），塞进去会互相打架。</para>
/// </summary>
public class NegiStackPanel : Panel
{
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            nameof(Orientation),
            typeof(Orientation),
            typeof(NegiStackPanel),
            new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(NegiStackPanel),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var vertical = Orientation == Orientation.Vertical;
        var spacing = Math.Max(0, Spacing);
        var childLimit = vertical
            ? new Size(availableSize.Width, double.PositiveInfinity)
            : new Size(double.PositiveInfinity, availableSize.Height);

        double main = 0;
        double cross = 0;
        var first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            if (!first) main += spacing;
            first = false;

            child.Measure(childLimit);
            var desired = child.DesiredSize;

            if (vertical)
            {
                main += desired.Height;
                cross = Math.Max(cross, desired.Width);
            }
            else
            {
                main += desired.Width;
                cross = Math.Max(cross, desired.Height);
            }
        }

        // 纵向：主轴上不设限（main 就是全部内容高度），横轴受可用宽度约束。
        // 横向反之。可用尺寸是无穷时不要把它当结果返回。
        double width, height;
        if (vertical)
        {
            width = double.IsInfinity(availableSize.Width) ? cross : Math.Min(cross, availableSize.Width);
            height = main;
        }
        else
        {
            width = main;
            height = double.IsInfinity(availableSize.Height) ? cross : Math.Min(cross, availableSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var vertical = Orientation == Orientation.Vertical;
        var spacing = Math.Max(0, Spacing);
        double offset = 0;
        var first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;

            if (!first) offset += spacing;
            first = false;

            var desired = child.DesiredSize;
            if (vertical)
            {
                child.Arrange(new Rect(0, offset, finalSize.Width, desired.Height));
                offset += desired.Height;
            }
            else
            {
                child.Arrange(new Rect(offset, 0, desired.Width, finalSize.Height));
                offset += desired.Width;
            }
        }

        return finalSize;
    }
}
