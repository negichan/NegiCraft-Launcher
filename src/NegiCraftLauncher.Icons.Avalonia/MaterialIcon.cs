using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace NegiCraftLauncher.Icons.Avalonia;

/// <summary>
/// 矢量图标控件。用法与原来的 <c>Material.Icons.Avalonia.MaterialIcon</c> 一致
/// （<c>Kind="Home" Width="16" Height="16" Foreground="..."</c>），所以除了 xmlns 那一行，
/// 所有 AXAML 都一个字不用改。
/// <para>
/// 这里**不用模板**，直接在 <see cref="Render"/> 里画：几何按 Material Design Icons 的 24x24
/// 视口给出，等比缩放并居中到控件尺寸（等价于原版模板里的 <c>Viewbox Stretch="Uniform"</c>）。
/// 少一个 ControlTheme 也就少一处需要 App.axaml 注册的东西。
/// </para>
/// </summary>
public class MaterialIcon : TemplatedControl
{
    /// <summary>Material Design Icons 的视口边长，所有 path 都按这个坐标系给出。</summary>
    private const double ViewBoxSize = 24.0;

    /// <summary>已解析的几何缓存（Avalonia 的 Geometry 没有 Freeze，用锁保护字典即可）。</summary>
    private static readonly Dictionary<MaterialIconKind, Geometry> GeometryCache = new();

    public static readonly StyledProperty<MaterialIconKind?> KindProperty =
        AvaloniaProperty.Register<MaterialIcon, MaterialIconKind?>(nameof(Kind));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<MaterialIcon, double>(nameof(IconSize), double.NaN);

    static MaterialIcon()
    {
        AffectsRender<MaterialIcon>(KindProperty, IconSizeProperty, ForegroundProperty);
        AffectsMeasure<MaterialIcon>(IconSizeProperty);

        // 原版靠 App.axaml 里的 <MaterialIconStyles/> 给默认前景色；我们不引样式文件，
        // 直接给个默认值。Foreground 是继承属性，父级设了值仍然会覆盖它。
        ForegroundProperty.OverrideDefaultValue<MaterialIcon>(Brushes.Black);
    }

    /// <summary>要显示的图标。null 时不画任何东西（与原控件一致）。</summary>
    public MaterialIconKind? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    /// <summary>显式图标尺寸。默认 NaN，表示取控件自身宽高里较小的一边。</summary>
    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // 没显式给宽高时退回视口尺寸，否则 TemplatedControl 的默认实现会返回 0x0、图标直接看不见。
        var box = double.IsNaN(IconSize) ? ViewBoxSize : IconSize;
        return new Size(
            double.IsNaN(Width) ? box : Width,
            double.IsNaN(Height) ? box : Height);
    }

    public override void Render(DrawingContext context)
    {
        var geometry = Kind is { } kind ? Resolve(kind) : null;
        if (geometry is null) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        // 等比缩放（Viewbox Stretch=Uniform 的语义）：取宽高能容纳的最大倍数，再居中。
        var box = double.IsNaN(IconSize) ? Math.Min(width, height) : IconSize;
        var scale = box / ViewBoxSize;
        var offsetX = (width - ViewBoxSize * scale) / 2;
        var offsetY = (height - ViewBoxSize * scale) / 2;

        using (context.PushTransform(
                   Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            context.DrawGeometry(Foreground, null, geometry);
        }
    }

    private static Geometry? Resolve(MaterialIconKind kind)
    {
        lock (GeometryCache)
        {
            if (GeometryCache.TryGetValue(kind, out var cached)) return cached;
        }

        if (!MaterialIconData.Paths.TryGetValue(kind, out var path)) return null;

        var geometry = PathGeometry.Parse(path);

        lock (GeometryCache)
        {
            GeometryCache[kind] = geometry;
        }

        return geometry;
    }
}
