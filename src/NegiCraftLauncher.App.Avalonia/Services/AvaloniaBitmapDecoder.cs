using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Avalonia.Services;

/// <summary>
/// Avalonia 侧的图片解码：按原尺寸解出来，缩放交给共享层（<c>PixelBuffer.Downsample</c>）。
///
/// <para>Avalonia 12 的 <c>Bitmap</c> 已经没有"解到指定尺寸"的重载了，而 WPF 有 ——
/// 如果各自按各自的能力缩，同一张图在两端就不是同一份像素，调色再准也对不齐。
/// 所以两端都只解原尺寸，缩放这一步走同一份共享代码。</para>
///
/// <para>Avalonia 的 <c>Bgra8888</c> 本身就是<b>预乘</b>的，<c>CopyPixels</c> 交出来直接就是
/// <see cref="PixelBuffer"/> 的约定，因此走 <see cref="PixelBuffer.FromPbgra"/>（不再乘一次 alpha）。
/// WPF 给的是直通的 <c>Bgra32</c>，那边才需要 <see cref="PixelBuffer.FromBgra"/>。</para>
/// </summary>
public sealed class AvaloniaBitmapDecoder : IBitmapDecoder
{
    /// <remarks>
    /// 这里<b>不 catch</b>：错误处理归调用方（VM 会把原因记进 <c>BackgroundDecodeState</c>，
    /// 调试桥的 <c>state</c> 直接打印）。自己吞掉的话，"壁纸为什么没出来"就永远查不到现场——
    /// 这个坑我在这儿踩过一次：一个 <c>catch { return null; }</c> 让排障绕了三圈。
    /// </remarks>
    public PixelBuffer Decode(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("路径为空", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到文件 [{path}] len={path.Length}", path);

        using var bmp = new Bitmap(path);
        var width = bmp.PixelSize.Width;
        var height = bmp.PixelSize.Height;
        if (width <= 0 || height <= 0) throw new InvalidDataException($"解出来是 {width}x{height}");

        var stride = width * 4;
        var bytes = new byte[stride * height];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            // 形参顺序是 (rect, buffer, **bufferSize**, **stride**) —— 按 WPF 的习惯把 stride
            // 放第三位会把它当成缓冲区大小，然后 stride=1 直接 ArgumentOutOfRangeException。
            bmp.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return PixelBuffer.FromPbgra(bytes, width, height);
    }
}
