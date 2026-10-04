using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.App.Converters;

/// <summary>
/// 把共享层的平台中立像素（<see cref="PixelBuffer"/>，预乘 BGRA）变成 WPF 的 <see cref="BitmapSource"/>。
///
/// <para>与 Avalonia 侧 <c>PixelBufferToBitmapConverter</c> 是同一个补偿点：VM 只持有字节，
/// 上屏这一步由每个平台各自完成。这里直接 <c>BitmapSource.Create</c>，不需要 Avalonia 那边的
/// <c>Lock()</c> + 手工拷贝 —— 托管数组的内存布局（小端 B,G,R,A）正好就是
/// <see cref="PixelFormats.Pbgra32"/>。</para>
/// </summary>
public sealed class PixelBufferToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PixelBuffer buffer || buffer.Width <= 0 || buffer.Height <= 0) return null;

        var bitmap = BitmapSource.Create(
            buffer.Width,
            buffer.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            buffer.Pixels,
            buffer.Width * 4);

        // 冻结之后可以跨线程用，也省掉一份无谓的拷贝。
        bitmap.Freeze();
        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>把磁盘路径变成 WPF 的 <see cref="BitmapImage"/>（用户自选的背景照片）。</summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(Path.GetFullPath(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // 用户可能选了张读不出来的图；让背景回落到生成图，而不是让绑定抛异常。
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// <c>bool</c> → <see cref="Visibility"/>。传 <c>invert</c> 取反，对应 Avalonia 的
/// <c>IsVisible="{Binding !X}"</c>。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// <c>value.ToString() == parameter</c>。对应 Avalonia 的
/// <c>{Binding X, Converter={x:Static ObjectConverters.Equal}, ConverterParameter='home'}</c>。
///
/// <para><b>返回值随目标类型走</b>：同一个转换器既给 <c>neg:Negi.Active</c>（要 <c>bool</c>）
/// 用，也给 <c>Visibility</c> 用。WPF 不会把 <c>bool</c> 隐式转成 <c>Visibility</c> ——
/// 转换失败时它<b>默默退回属性的默认值</b>（<c>Visible</c>），四个页面会叠在一起显示，
/// 而且一条错误都不报。所以这里必须自己看 <paramref name="targetType"/>。</para>
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return AsTarget(equal, targetType);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>bool 结果按目标类型包装（<c>Visibility</c> 或原样 <c>bool</c>）。</summary>
    internal static object AsTarget(bool flag, Type targetType) =>
        targetType == typeof(Visibility) ? (flag ? Visibility.Visible : Visibility.Collapsed) : flag;
}

/// <summary>非 null → <c>true</c>。对应 Avalonia 的 <c>ObjectConverters.IsNotNull</c>。</summary>
public sealed class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        EqualsConverter.AsTarget(value is not null, targetType);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → <c>true</c>。对应 Avalonia 的 <c>ObjectConverters.IsNull</c>。</summary>
public sealed class IsNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        EqualsConverter.AsTarget(value is null, targetType);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
