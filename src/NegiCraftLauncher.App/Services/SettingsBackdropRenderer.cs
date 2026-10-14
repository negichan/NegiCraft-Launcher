using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace NegiCraftLauncher.App.Services;

internal static class SettingsBackdropRenderer
{
    internal static BitmapSource? Capture(FrameworkElement scene)
    {
        if (scene.ActualWidth <= 0 || scene.ActualHeight <= 0) return null;

        const double scale = 0.25;
        const double blurRadius = 4.5;
        var width = Math.Max(1, (int)Math.Ceiling(scene.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(scene.ActualHeight * scale));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            // Content bounds include shadows and overflow; capture the layout viewport instead.
            var brush = new VisualBrush(scene)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(scene.RenderSize),
                Stretch = Stretch.Fill,
            };
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        var raw = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        raw.Render(drawing);
        raw.Freeze();

        // Extend the edge pixels so the blur cannot mix in transparent pixels outside the viewport.
        var padding = (int)Math.Ceiling(blurRadius * 2);
        var paddedWidth = width + padding * 2;
        var paddedHeight = height + padding * 2;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        raw.CopyPixels(pixels, stride, 0);
        var paddedStride = paddedWidth * 4;
        var paddedPixels = new byte[paddedStride * paddedHeight];
        for (var y = 0; y < paddedHeight; y++)
        {
            var sourceRow = Math.Clamp(y - padding, 0, height - 1) * stride;
            for (var x = 0; x < paddedWidth; x++)
            {
                var source = sourceRow + Math.Clamp(x - padding, 0, width - 1) * 4;
                var target = y * paddedStride + x * 4;
                Buffer.BlockCopy(pixels, source, paddedPixels, target, 4);
            }
        }
        var padded = BitmapSource.Create(paddedWidth, paddedHeight, 96, 96,
            PixelFormats.Pbgra32, null, paddedPixels, paddedStride);
        padded.Freeze();
        var image = new Image
        {
            Source = padded,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect { Radius = blurRadius, RenderingBias = RenderingBias.Performance },
        };
        // Render an unaffected parent to keep effect bounds from shifting the bitmap origin.
        var frame = new Grid { Width = paddedWidth, Height = paddedHeight };
        frame.Children.Add(image);
        frame.Measure(new Size(paddedWidth, paddedHeight));
        frame.Arrange(new Rect(0, 0, paddedWidth, paddedHeight));
        var blurred = new RenderTargetBitmap(paddedWidth, paddedHeight, 96, 96, PixelFormats.Pbgra32);
        blurred.Render(frame);
        blurred.Freeze();
        var result = new CroppedBitmap(blurred, new Int32Rect(padding, padding, width, height));
        result.Freeze();
        return result;
    }
}
