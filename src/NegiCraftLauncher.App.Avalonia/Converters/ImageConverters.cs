using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.App.Avalonia.Converters;

/// <summary>
/// 把共享层的平台中立像素（<see cref="PixelBuffer"/>，预乘 BGRA）变成 Avalonia 的 <see cref="Bitmap"/>。
///
/// <para>这是 P0「VM 去 UI 类型化」之后视图层唯一的补偿点：VM 只持有字节，
/// 上屏这一步由每个平台各自完成（WPF 侧对应的是 <c>BitmapSource.Create</c>）。</para>
///
/// <para>转换结果按 <see cref="PixelBuffer"/> <b>引用</b>缓存（与 WPF 侧同一个策略）：
/// VM 里的图标都来自 <c>BlockAssets</c> / <c>EmbeddedImages</c> 的缓存，同一个方块只有一份
/// <see cref="PixelBuffer"/>，而绑定会在列表重排、滚动虚拟化、切页时反复求值 ——
/// 源图现在是 1024x1024，每次都重建一遍是纯浪费。</para>
/// </summary>
public sealed class PixelBufferToBitmapConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<PixelBuffer, WriteableBitmap> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PixelBuffer buffer) return null;

        if (Cache.TryGetValue(buffer, out var cached)) return cached;

        var bitmap = new WriteableBitmap(
            new PixelSize(buffer.Width, buffer.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var fb = bitmap.Lock();
        var rowBytes = buffer.Width * 4;

        unsafe
        {
            var src = buffer.Pixels;
            var dst = (byte*)fb.Address;

            for (var y = 0; y < buffer.Height; y++)
            {
                fixed (uint* row = &src[y * buffer.Width])
                {
                    Buffer.MemoryCopy(row, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
                }
            }
        }

        Cache.Add(buffer, bitmap);
        return bitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>把磁盘路径变成 Avalonia 的 <see cref="Bitmap"/>（用户自选的背景照片）。</summary>
public sealed class PathToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || path.Length == 0) return null;

        try
        {
            return new Bitmap(path);
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
