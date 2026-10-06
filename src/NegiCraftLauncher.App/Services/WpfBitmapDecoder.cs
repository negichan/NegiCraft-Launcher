using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// WPF 侧的图片解码：把文件解成平台中立的 <see cref="PixelBuffer"/>，好让"调色"只在共享层做一份。
///
/// <para><b>为什么不在 XAML 里直接绑路径</b>：那样每个绘制点各自解码一次（主层 / 侧栏背板 /
/// 设置缩略图 = 三次），而且调色就没有地方做了 —— 色彩矩阵必须两端逐位一致，只能落在
/// <c>Raster</c>。见 <see cref="IBitmapDecoder"/>。</para>
///
/// <para>输出走 <c>Bgra32</c>（<b>直通</b> alpha）而不是 <c>Pbgra32</c>：直通→预乘的换算统一由
/// <c>PixelBuffer.FromBgra</c> 在共享层做，两端才不会各乘一遍、乘出两种结果。</para>
/// </summary>
public sealed class WpfBitmapDecoder : IBitmapDecoder
{
    /// <inheritdoc>
    /// 不 catch：错误处理归调用方（VM 会把原因记进 <c>BackgroundDecodeState</c>）。
    /// </inheritdoc>
    public PixelBuffer Decode(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("路径为空", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到文件 [{path}]", path);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        // OnLoad：一次读满并释放文件句柄，用户之后移动/删除原图都不影响已显示的壁纸。
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        // ⚠️ 不要用 DecodePixelWidth/Height 在这里缩：见 IBitmapDecoder 的说明，
        //    两个维度同时给还会被当成"精确目标尺寸"把图拉变形（实测 2000x1200 → 2560x2560）。
        bmp.UriSource = new Uri(path, UriKind.Absolute);
        bmp.EndInit();
        bmp.Freeze();

        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        if (width <= 0 || height <= 0) throw new InvalidDataException($"解出来是 {width}x{height}");

        var stride = width * 4;
        var bytes = new byte[stride * height];
        converted.CopyPixels(bytes, stride, 0);
        return PixelBuffer.FromBgra(bytes, width, height);
    }
}
