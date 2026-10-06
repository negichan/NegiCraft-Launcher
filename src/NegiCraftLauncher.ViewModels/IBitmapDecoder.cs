using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.ViewModels;

/// <summary>
/// 由视图层注入的图片解码器。
///
/// <para><b>为什么要反向注入</b>：共享层刻意零 UI 依赖，也几乎没有图片解码能力 ——
/// <c>Raster/PngCodec.cs</c> 只认 PNG，而壁纸选择器收的是 png/jpg/jpeg/webp/bmp。
/// 可"调色"又必须放在共享层，否则 WPF 与 Avalonia 各写一遍，同一组参数会调出两种颜色。
/// 于是把"解码成平台中立像素"这一步交给各端实现，调色数学只留一份。</para>
///
/// <para>注入点在 <c>App.xaml.cs</c>（WPF）与 <c>App.axaml.cs</c>（Avalonia）。
/// 没注入（离屏探针、单元测试）就安静地不出自选壁纸，回落到内置生成场景。</para>
/// </summary>
public interface IBitmapDecoder
{
    /// <summary>
    /// 解一张图成 <see cref="PixelBuffer"/>（预乘 BGRA），<b>按原尺寸</b>。
    /// </summary>
    /// <remarks>
    /// 失败要<b>抛</b>，不要静默返回 <c>null</c>：调用方（VM）负责把原因记进
    /// <c>BackgroundDecodeState</c> 并让首页回落到内置场景。解码器自己吞异常的话，
    /// "壁纸为什么没出来"就没有现场可查（这条踩过一次）。
    ///
    /// <para>这里刻意不提供"解到指定尺寸"：WPF 能解码时缩，Avalonia 12 的 <c>Bitmap</c> 没有那个重载，
    /// 两边会解出两份不同分辨率的像素，调色再准也对不齐。缩放统一由调用方用
    /// <c>PixelBuffer.Downsample</c> 做。</para>
    /// </remarks>
    /// <exception cref="System.IO.FileNotFoundException">文件不存在。</exception>
    PixelBuffer Decode(string path);
}
