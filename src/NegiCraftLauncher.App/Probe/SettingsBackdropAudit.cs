using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NegiCraftLauncher.App.Services;

namespace NegiCraftLauncher.App.Probe;

internal static class SettingsBackdropAudit
{
    internal static void Run(StringBuilder log)
    {
        foreach (var size in new[] { new Size(1180, 720), new Size(960, 620), new Size(961, 621) })
        {
            var scene = new Grid { Width = size.Width, Height = size.Height, Background = Brushes.White };
            // Overflow deliberately changes visual content bounds without changing layout bounds.
            scene.Children.Add(new Border
            {
                Width = 80, Height = 80, Background = Brushes.Magenta,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransform = new TranslateTransform(80, 80),
            });
            foreach (var (horizontal, vertical, color) in new[]
            {
                (HorizontalAlignment.Left, VerticalAlignment.Top, Brushes.Red),
                (HorizontalAlignment.Right, VerticalAlignment.Top, Brushes.Lime),
                (HorizontalAlignment.Left, VerticalAlignment.Bottom, Brushes.Blue),
                (HorizontalAlignment.Right, VerticalAlignment.Bottom, Brushes.Yellow),
            })
                scene.Children.Add(new Border
                {
                    Width = 120, Height = 120, Background = color,
                    HorizontalAlignment = horizontal, VerticalAlignment = vertical,
                });
            scene.Measure(size);
            scene.Arrange(new Rect(size));
            scene.UpdateLayout();

            var bitmap = SettingsBackdropRenderer.Capture(scene)!;
            var stride = bitmap.PixelWidth * 4;
            var pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            var transparent = 0;
            for (var i = 3; i < pixels.Length; i += 4)
                if (pixels[i] < 254) transparent++;

            bool ColorAt(int x, int y, byte r, byte g, byte b)
            {
                var index = y * stride + x * 4;
                return Math.Abs(pixels[index] - b) <= 3 &&
                       Math.Abs(pixels[index + 1] - g) <= 3 &&
                       Math.Abs(pixels[index + 2] - r) <= 3;
            }
            var corners = ColorAt(12, 12, 255, 0, 0) &&
                          ColorAt(bitmap.PixelWidth - 13, 12, 0, 255, 0) &&
                          ColorAt(12, bitmap.PixelHeight - 13, 0, 0, 255) &&
                          ColorAt(bitmap.PixelWidth - 13, bitmap.PixelHeight - 13, 255, 255, 0);
            log.AppendLine($"  {(transparent == 0 ? "OK" : "!!")} settings backdrop {size}: opaque edge coverage (transparent pixels={transparent})");
            log.AppendLine($"  {(corners ? "OK" : "!!")} settings backdrop {size}: corner markers keep their position despite visual overflow");
        }
    }
}
