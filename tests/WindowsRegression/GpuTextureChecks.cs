using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MinecraftSkinRender;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;
using NegiCraftLauncher.Skin.Rendering;

internal static class GpuTextureChecks
{
    public static void Run(Action<string, bool> check)
    {
        foreach (var type in new[] { SkinType.New, SkinType.NewSlim })
        foreach (var upscale in new[] { 1, 8 })
        foreach (var flexible in new[] { false, true })
        foreach (var jointed in new[] { false, true })
        foreach (var alpha in new byte[] { 0, 96, 255 })
        {
            var rgba = new byte[64 * 64 * 4];
            var overlayMask = new bool[64 * 64];
            Array.Fill(rgba, (byte)255);
            foreach (var part in SkinModel.Build(type, top: true, jointed))
            {
                var quads = new List<SkinQuad>();
                SkinModel.AppendQuads(part, quads);
                foreach (var quad in quads)
                {
                    var min = Vector2.Min(Vector2.Min(quad.T0, quad.T1), Vector2.Min(quad.T2, quad.T3));
                    var max = Vector2.Max(Vector2.Max(quad.T0, quad.T1), Vector2.Max(quad.T2, quad.T3));
                    for (var y = (int)MathF.Round(min.Y * 64); y < (int)MathF.Round(max.Y * 64); y++)
                    for (var x = (int)MathF.Round(min.X * 64); x < (int)MathF.Round(max.X * 64); x++)
                    {
                        rgba[(y * 64 + x) * 4 + 3] = alpha;
                        overlayMask[y * 64 + x] = true;
                    }
                }
            }

            var renderer = new SkinRenderGpu
            {
                TextureUpscale = upscale,
                SpineFlexible = flexible,
                LimbFlexible = flexible,
                LimbJoints = jointed
            };
            renderer.SetSkin(SkinTexture.FromRgba(rgba, 64, 64), type);
            renderer.EnsureModel();
            check($"GPU overlay keeps only populated parts (alpha={alpha}, {type}, upscale={upscale}, " +
                  $"flexible={flexible}, jointed={jointed})",
                alpha == 0 ? renderer.TopLayer.Count == 0 : renderer.TopLayer.Count > 0);
            var texture = (BitmapSource)renderer.TopTexture!;
            var pixels = new byte[texture.PixelWidth * texture.PixelHeight * 4];
            texture.CopyPixels(pixels, texture.PixelWidth * 4, 0);
            var isolated = true;
            for (var y = 0; y < texture.PixelHeight; y++)
            for (var x = 0; x < texture.PixelWidth; x++)
            {
                var expectedAlpha = overlayMask[(y / upscale) * 64 + x / upscale] ? alpha : 0;
                isolated &= pixels[(y * texture.PixelWidth + x) * 4 + 3] == expectedAlpha;
            }
            check($"GPU overlay atlas excludes base pixels and preserves alpha ({type}, alpha={alpha}, " +
                  $"upscale={upscale}, flexible={flexible}, jointed={jointed})", isolated);
            var maxError = 0.0;
            foreach (var (_, mesh) in renderer.TopLayer)
            {
                var coordinates = mesh.TextureCoordinates;
                for (var i = 0; i < coordinates.Count; i += 4)
                for (var edge = 0; edge < 4; edge++)
                for (var step = 0; step <= 4; step++)
                {
                    var a = coordinates[i + edge];
                    var b = coordinates[i + (edge + 1) % 4];
                    var t = step / 4.0;
                    var sample = new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    maxError = Math.Max(maxError,
                        Math.Abs(SampleAlpha(texture, pixels, sample) - alpha));
                }
            }

            check($"GPU overlay alpha={alpha}, {type}, upscale={upscale}, flexible={flexible}, " +
                  $"jointed={jointed} stays inside its atlas faces (error={maxError:F3})", maxError < 0.001);

            if (flexible && !jointed)
            {
                var continuous = true;
                foreach (var (_, mesh) in renderer.TopLayer)
                {
                    // Each side's neighboring strips must meet at the same UV after remapping.
                    var segments = (mesh.Positions.Count / 4 - 2) / 4;
                    for (var side = 0; side < 4; side++)
                    for (var segment = 0; segment < segments - 1; segment++)
                    {
                        var i = (side * segments + segment) * 4;
                        continuous &= mesh.TextureCoordinates[i + 3] == mesh.TextureCoordinates[i + 4];
                        continuous &= mesh.TextureCoordinates[i + 2] == mesh.TextureCoordinates[i + 5];
                    }
                }
                check($"GPU subdivided texture stays continuous ({type}, upscale={upscale}, alpha={alpha})",
                    continuous);
            }

            if (type == SkinType.New && upscale == 8 && flexible && !jointed && alpha == 0)
                CheckRenderedOverlay(renderer, check);
        }

        CheckSparseOverlay(check);

        var args = Environment.GetCommandLineArgs();
        var outputIndex = Array.IndexOf(args, "--texture-preview");
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
            SavePreview(args[outputIndex + 1]);
    }

    private static void CheckRenderedOverlay(SkinRenderGpu renderer, Action<string, bool> check)
    {
        var pose = new SkinPoseDriver(renderer);
        pose.Reset();
        var viewport = new SkinGpuViewport();
        foreach (var width in new[] { 83, 110, 165 })
        foreach (var pitch in new[] { -25, 0, 15, 25 })
        foreach (var yaw in new[] { 0, 45, 80 })
        {
            renderer.Width = width;
            renderer.Height = (int)Math.Round(width * 171.0 / 110);
            pose.SetHeadLookAt(pitch, yaw);
            pose.Update(0);
            renderer.EnableTop = false;
            var expected = Render(renderer, viewport);
            renderer.EnableTop = true;
            var actual = Render(renderer, viewport);
            var expectedPixels = new byte[width * renderer.Height * 4];
            var actualPixels = new byte[expectedPixels.Length];
            expected.CopyPixels(expectedPixels, width * 4, 0);
            actual.CopyPixels(actualPixels, width * 4, 0);
            var args = Environment.GetCommandLineArgs();
            var outputIndex = Array.IndexOf(args, "--texture-preview");
            if (width == 110 && pitch == 25 && yaw == 45 && outputIndex >= 0 && outputIndex + 1 < args.Length)
            {
                SaveImage(expected, args[outputIndex + 1] + ".base.png");
                SaveImage(actual, args[outputIndex + 1] + ".overlay.png");
            }
            var changed = 0;
            var maxError = 0;
            var extraPixels = 0;
            for (var i = 0; i < actualPixels.Length; i++)
            {
                if (actualPixels[i] != expectedPixels[i]) changed++;
                maxError = Math.Max(maxError, Math.Abs(actualPixels[i] - expectedPixels[i]));
                if (i % 4 == 3 && expectedPixels[i] == 0 && actualPixels[i] != 0) extraPixels++;
            }
            check($"GPU empty overlay is invisible at width={width}, pitch={pitch}, yaw={yaw} " +
                  $"(changed channels={changed}, max error={maxError}, extra pixels={extraPixels})",
                changed == 0 && extraPixels == 0 && expectedPixels.Any(value => value > 0));
        }
    }

    private static void CheckSparseOverlay(Action<string, bool> check)
    {
        foreach (var type in new[] { SkinType.New, SkinType.NewSlim })
        foreach (var alpha in new byte[] { 1, 96, 255 })
        {
            var head = SkinModel.Build(type, top: true).Single(part => part.Type == ModelPartType.Head);
            var quads = new List<SkinQuad>();
            SkinModel.AppendQuads(head, quads);
            var face = quads[0];
            var uv = (face.T0 + face.T1 + face.T2 + face.T3) / 4;
            var x = (int)(uv.X * 64);
            var y = (int)(uv.Y * 64);
            var skin = SkinTexture.FromRgba(new byte[64 * 64 * 4], 64, 64);
            skin.Pixels[y * 64 + x] = ((uint)alpha << 24) | 0x00FFFFFFu;
            var renderer = new SkinRenderGpu
            {
                TextureUpscale = 8, SpineFlexible = true, LimbFlexible = true
            };
            renderer.SetSkin(skin, type);
            renderer.EnsureModel();
            var pixel = new byte[4];
            ((BitmapSource)renderer.TopTexture!).CopyPixels(new Int32Rect(x * 8, y * 8, 1, 1), pixel, 4, 0);
            check($"GPU preserves a single populated hat face ({type}, alpha={alpha})",
                renderer.TopLayer.Count == 1 && renderer.TopLayer[0].Part == ModelPartType.Head &&
                renderer.TopLayer[0].Mesh.Positions.Count == 4 && pixel[3] == alpha);

            renderer.SetSkin(SkinTexture.FromRgba(new byte[64 * 64 * 4], 64, 64), type);
            renderer.EnsureModel();
            check($"GPU reload removes stale overlay faces ({type}, alpha={alpha})",
                renderer.TopLayer.Count == 0);
        }
    }

    private static RenderTargetBitmap Render(SkinRenderGpu renderer, SkinGpuViewport viewport)
    {
        viewport.Sync(renderer);
        viewport.View.Measure(new Size(renderer.Width, renderer.Height));
        viewport.View.Arrange(new Rect(0, 0, renderer.Width, renderer.Height));
        var target = new RenderTargetBitmap(renderer.Width, renderer.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(viewport.View);
        return target;
    }

    private static void SaveImage(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double SampleAlpha(BitmapSource texture, byte[] pixels, Point uv)
    {
        var x = uv.X * texture.PixelWidth - 0.5;
        var y = uv.Y * texture.PixelHeight - 0.5;
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var tx = x - x0;
        var ty = y - y0;
        double Alpha(int px, int py)
        {
            px = (px % texture.PixelWidth + texture.PixelWidth) % texture.PixelWidth;
            py = (py % texture.PixelHeight + texture.PixelHeight) % texture.PixelHeight;
            return pixels[(py * texture.PixelWidth + px) * 4 + 3];
        }
        return (Alpha(x0, y0) * (1 - tx) + Alpha(x0 + 1, y0) * tx) * (1 - ty) +
               (Alpha(x0, y0 + 1) * (1 - tx) + Alpha(x0 + 1, y0 + 1) * tx) * ty;
    }

    private static void SavePreview(string path)
    {
        var renderer = new SkinRenderGpu
        {
            Width = 330, Height = 513, TextureUpscale = 8,
            SpineFlexible = true, LimbFlexible = true
        };
        renderer.SetSkin(SkinTexture.Decode(DefaultSkins.Bytes(false))!, SkinType.New);
        var pose = new SkinPoseDriver(renderer);
        pose.Reset();
        pose.SetHeadLookAt(25, 55);
        pose.Update(0);
        var viewport = new SkinGpuViewport();
        viewport.Sync(renderer);
        viewport.View.Measure(new Size(330, 513));
        viewport.View.Arrange(new Rect(0, 0, 330, 513));
        var target = new RenderTargetBitmap(330, 513, 96, 96, PixelFormats.Pbgra32);
        target.Render(viewport.View);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
