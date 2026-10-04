using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NegiCraftLauncher.Icons;

/// <summary>
/// 矢量图标控件。API 与 <c>Material.Icons.WPF.MaterialIcon</c> 对齐（<see cref="Kind"/>、
/// <see cref="IconSize"/>、只读的 <see cref="Geometry"/>），所以 XAML 里除了 xmlns 那一行，
/// <c>Kind="Home" Width="16" Height="16" Foreground="..."</c> 之类的用法一个字都不用改。
/// <para>
/// 几何数据来自构建时生成的 <c>Generated/IconData.g.cs</c>：只包含本仓库 XAML 里实际用到的图标，
/// 而不是 Material.Icons 那 13645 个（那是 5.71 MB，占安装包 78%）。
/// </para>
/// </summary>
public class MaterialIcon : Control
{
    /// <summary>已解析的几何缓存。Freeze 之后可以跨线程共享，避免每次改 Kind 都重新解析路径。</summary>
    private static readonly Dictionary<MaterialIconKind, Geometry> GeometryCache = new();

    static MaterialIcon()
    {
        // 模板在 Themes/Generic.xaml（由 AssemblyInfo.cs 的 ThemeInfo 指到本程序集）。
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MaterialIcon), new FrameworkPropertyMetadata(typeof(MaterialIcon)));
    }

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(nameof(Kind), typeof(MaterialIconKind?), typeof(MaterialIcon),
            new PropertyMetadata(null, OnKindChanged));

    /// <summary>要显示的图标。null 时不画任何东西（与原控件一致）。</summary>
    public MaterialIconKind? Kind
    {
        get => (MaterialIconKind?)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(MaterialIcon),
            new PropertyMetadata(double.NaN));

    /// <summary>显式图标尺寸。默认 NaN，表示交给 Width/Height（都没有时用 FontSize）。</summary>
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    private static readonly DependencyPropertyKey GeometryPropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(Geometry), typeof(Geometry), typeof(MaterialIcon),
            new PropertyMetadata(Geometry.Empty));

    /// <summary>由 <see cref="Kind"/> 解析出来的几何，供模板里的 Path 绑定。</summary>
    public Geometry Geometry => (Geometry)GetValue(GeometryPropertyKey.DependencyProperty);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MaterialIcon)d).UpdateGeometry();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateGeometry();
    }

    private void UpdateGeometry()
    {
        var geometry = Kind is { } kind ? Resolve(kind) : Geometry.Empty;
        SetValue(GeometryPropertyKey, geometry);
    }

    private static Geometry Resolve(MaterialIconKind kind)
    {
        lock (GeometryCache)
        {
            if (GeometryCache.TryGetValue(kind, out var cached)) return cached;
        }

        if (!MaterialIconData.Paths.TryGetValue(kind, out var path)) return Geometry.Empty;

        // Geometry.Parse 必须走 UI 线程（Freezable 有线程亲和），调用点都在控件属性变更/模板应用里。
        var geometry = Geometry.Parse(path);
        geometry.Freeze();

        lock (GeometryCache)
        {
            GeometryCache[kind] = geometry;
        }

        return geometry;
    }
}
