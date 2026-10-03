using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;

namespace NegiCraftLauncher.App.Wpf.Probe;

/// <summary>
/// P2：软件光栅化器的实测 benchmark。
///
/// <para>这是"会不会卡"唯一的实测依据 —— 在写正式渲染器之前先量真实 ms/帧，
/// 再据此决定超采样倍数、要不要脏矩形、要不要独立渲染线程。</para>
///
/// <para>测两段：<b>光栅化</b>（<see cref="SoftwareRenderer"/> 到 <see cref="PixelBuffer"/>）
/// 与 <b>上屏</b>（<c>WriteableBitmap.WritePixels</c>）。分开量才知道瓶颈在哪。</para>
/// </summary>
internal static class Bench
{
    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "ncl-wpf-bench.txt");

    /// <summary>桌宠窗口的尺寸；2× 超采样即内部 480×960 ≈ 46 万像素/帧。</summary>
    private const int Width = 240;
    private const int Height = 480;
    private const int SampleScale = 2;
    private const int WarmupFrames = 20;
    private const int MeasuredFrames = 300;

    public static void Run()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif

        var log = new StringBuilder();
        log.AppendLine("NegiCraft Launcher — 软件光栅化 benchmark");
        log.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"构建配置: {configuration}");
#if DEBUG
        // 用 #if 而不是运行期 if：Release 下常量折叠会让分支变成不可达代码（CS0162）。
        log.AppendLine("  ⚠ Debug 构建下 Roslyn 不做内联/死代码消除，实测会翻倍（Debug 6.7ms vs Release 3.4ms）。");
        log.AppendLine("    性能结论一律以 Release 为准：dotnet build -c Release && NegiCraftLauncher.exe --bench");
#endif
        log.AppendLine($"画布: {Width}x{Height}，超采样 {SampleScale}x（内部 {Width * SampleScale}x{Height * SampleScale}）");
        log.AppendLine($"帧数: 预热 {WarmupFrames} + 计时 {MeasuredFrames}");
        log.AppendLine();

        var texture = DefaultSkins.Pixels(slim: false);
        var quads = BoxMesh.BuildBenchmarkCharacter(withOverlay: true);
        log.AppendLine($"几何: {quads.Count} 个四边形（6 个身体部件 + 6 个覆盖层部件）");
        log.AppendLine();

        var renderer = new SoftwareRenderer();
        renderer.Configure(Width, Height, SampleScale);

        var target = new PixelBuffer(Width, Height);

        var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 3.4f), new Vector3(0f, 0f, 0f), Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4f, (float)Width / Height, 0.1f, 100f);

        // 超采样后的视口尺寸：顶点要投到内部分辨率上。
        float vw = renderer.InternalWidth;
        float vh = renderer.InternalHeight;

        var clear = new double[MeasuredFrames];
        var draw = new double[MeasuredFrames];
        var resolve = new double[MeasuredFrames];
        var raster = new double[MeasuredFrames];
        var blit = new double[MeasuredFrames];
        var total = new double[MeasuredFrames];

        var bitmap = new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32, null);
        var stride = Width * 4;

        for (var frame = 0; frame < WarmupFrames + MeasuredFrames; frame++)
        {
            var yaw = frame * 0.02f;
            var model = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(0f, -0.95f, 0f);
            var mvp = model * view * projection;

            var totalWatch = Stopwatch.StartNew();

            var rasterWatch = Stopwatch.StartNew();

            var clearWatch = Stopwatch.StartNew();
            renderer.Begin(0x00000000);
            clearWatch.Stop();

            var drawWatch = Stopwatch.StartNew();
            foreach (var quad in quads)
            {
                var a = Projector.Project(mvp, quad.P0.X, quad.P0.Y, quad.P0.Z, quad.T0.X, quad.T0.Y, vw, vh);
                var b = Projector.Project(mvp, quad.P1.X, quad.P1.Y, quad.P1.Z, quad.T1.X, quad.T1.Y, vw, vh);
                var c = Projector.Project(mvp, quad.P2.X, quad.P2.Y, quad.P2.Z, quad.T2.X, quad.T2.Y, vw, vh);
                var d = Projector.Project(mvp, quad.P3.X, quad.P3.Y, quad.P3.Z, quad.T3.X, quad.T3.Y, vw, vh);
                renderer.DrawQuad(a, b, c, d, texture, 64, 64);
            }

            drawWatch.Stop();

            var resolveWatch = Stopwatch.StartNew();
            renderer.ResolveTo(target);
            resolveWatch.Stop();
            rasterWatch.Stop();

            var blitWatch = Stopwatch.StartNew();
            bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), target.Pixels, stride, 0);
            blitWatch.Stop();

            totalWatch.Stop();

            if (frame >= WarmupFrames)
            {
                var i = frame - WarmupFrames;
                clear[i] = clearWatch.Elapsed.TotalMilliseconds;
                draw[i] = drawWatch.Elapsed.TotalMilliseconds;
                resolve[i] = resolveWatch.Elapsed.TotalMilliseconds;
                raster[i] = rasterWatch.Elapsed.TotalMilliseconds;
                blit[i] = blitWatch.Elapsed.TotalMilliseconds;
                total[i] = totalWatch.Elapsed.TotalMilliseconds;
            }
        }

        Report(log, "  · 清屏（Begin）", clear);
        Report(log, "  · 投影 + 光栅化 72 个四边形", draw);
        Report(log, "  · 超采样降采样（ResolveTo）", resolve);
        Report(log, "光栅化合计（Begin + 72 个四边形 + 降采样）", raster);
        Report(log, "上屏（WriteableBitmap.WritePixels）", blit);
        Report(log, "合计（每帧）", total);

        var mean = Mean(total);
        log.AppendLine();
        log.AppendLine(mean < 4.0
            ? $"结论: 平均 {mean:F2} ms/帧 < 4 ms —— 达标，60fps 有充足余量（预算 16.7ms）。"
            : $"结论: 平均 {mean:F2} ms/帧 ≥ 4 ms —— 需要降级（先试关超采样 / 只清脏矩形 / 独立渲染线程）。");

        // 存一张图肉眼核对几何与朝向。
        try
        {
            var preview = Path.Combine(Path.GetTempPath(), "ncl-raster-preview.png");
            File.WriteAllBytes(preview, PngCodec.Encode(target));
            log.AppendLine($"预览图: {preview}");

            // 四视角对照图：正 / 右 / 背 / 左。镜像与背面剔除是否正确，看这张一眼就能判。
            var sheet = Path.Combine(Path.GetTempPath(), "ncl-raster-views.png");
            File.WriteAllBytes(sheet, PngCodec.Encode(RenderViewSheet(renderer, view, projection, vw, vh, texture)));
            log.AppendLine($"四视角: {sheet}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"预览图写入失败: {ex.Message}");
        }

        File.WriteAllText(ResultPath, log.ToString());
    }

    /// <summary>
    /// 渲染 正/右/背/左 四个朝向，横向拼成一张 4× 宽的图。
    /// 拼图比四张单图好核对：一眼就能看出模型有没有被镜像、背面是不是背面。
    /// </summary>
    private static PixelBuffer RenderViewSheet(
        SoftwareRenderer renderer, Matrix4x4 view, Matrix4x4 projection, float vw, float vh, uint[] texture)
    {
        var quads = BoxMesh.BuildBenchmarkCharacter(withOverlay: true);
        var frame = new PixelBuffer(Width, Height);
        var sheet = new PixelBuffer(Width * 4, Height);

        for (var i = 0; i < 4; i++)
        {
            var model = Matrix4x4.CreateRotationY(i * MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, -0.95f, 0f);
            var mvp = model * view * projection;

            renderer.Begin(0x00000000);
            foreach (var quad in quads)
            {
                var a = Projector.Project(mvp, quad.P0.X, quad.P0.Y, quad.P0.Z, quad.T0.X, quad.T0.Y, vw, vh);
                var b = Projector.Project(mvp, quad.P1.X, quad.P1.Y, quad.P1.Z, quad.T1.X, quad.T1.Y, vw, vh);
                var c = Projector.Project(mvp, quad.P2.X, quad.P2.Y, quad.P2.Z, quad.T2.X, quad.T2.Y, vw, vh);
                var d = Projector.Project(mvp, quad.P3.X, quad.P3.Y, quad.P3.Z, quad.T3.X, quad.T3.Y, vw, vh);
                renderer.DrawQuad(a, b, c, d, texture, 64, 64);
            }

            renderer.ResolveTo(frame);

            for (var y = 0; y < Height; y++)
            {
                Array.Copy(frame.Pixels, y * Width, sheet.Pixels, y * sheet.Width + i * Width, Width);
            }
        }

        return sheet;
    }

    private static void Report(StringBuilder log, string label, double[] samples)
    {
        var sorted = (double[])samples.Clone();
        Array.Sort(sorted);

        log.AppendLine($"{label}");
        log.AppendLine($"  平均 {Mean(samples):F3} ms   " +
                       $"p50 {Percentile(sorted, 0.50):F3}   " +
                       $"p95 {Percentile(sorted, 0.95):F3}   " +
                       $"p99 {Percentile(sorted, 0.99):F3}   " +
                       $"最大 {sorted[^1]:F3}");
    }

    private static double Mean(double[] values)
    {
        var sum = 0.0;
        foreach (var v in values) sum += v;
        return sum / values.Length;
    }

    private static double Percentile(double[] sorted, double q)
    {
        var index = (int)Math.Clamp(Math.Round(q * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }
}
