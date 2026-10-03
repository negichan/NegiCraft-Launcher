using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;

namespace NegiCraftLauncher.App.Wpf.Probe;

/// <summary>
/// P3 的出图工具：不启动任何窗口，直接把一张皮肤 PNG 用软件光栅化渲染出来存盘，
/// 用来和 Avalonia/OpenGL 版的截图肉眼对齐。
///
/// <para>用法：<c>NegiCraftLauncher.exe --skin &lt;皮肤png&gt; [--out &lt;输出png&gt;] [--w 165 --h 257]</c>。
/// 默认输出 <c>%TEMP%\ncl-skin-software.png</c>（四视角拼图）与 <c>...-front.png</c>（单正面）。
/// 同时把解析出的皮肤类型、贴图尺寸、每个视角的旋转角写进 <c>%TEMP%\ncl-skin-preview.txt</c>。</para>
/// </summary>
internal static class SkinPreview
{
    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "ncl-skin-preview.txt");

    private static readonly string ComparePath =
        Path.Combine(Path.GetTempPath(), "ncl-skin-compare.txt");

    /// <summary>GL 版皮肤预览控件的实际像素尺寸（165x257），照抄过来才好逐像素对比。</summary>
    private const int DefaultWidth = 165;
    private const int DefaultHeight = 257;

    /// <summary>视角角度（度）。0 = 正面，90/180/270 依次绕 Y 轴。</summary>
    private static readonly float[] ViewAngles = [0f, 90f, 180f, 270f];

    public static void Run(string[] args)
    {
        // --compare a.png b.png：把两张图（都合成到白底上）逐像素比，给出差异统计。
        var left = GetOption(args, "--compare");
        if (left != null)
        {
            Compare(left, GetOption(args, "--with") ?? "");
            return;
        }

        var log = new StringBuilder();

        var skinPath = GetOption(args, "--skin");
        if (string.IsNullOrEmpty(skinPath) || !File.Exists(skinPath))
        {
            log.AppendLine($"皮肤文件不存在: {skinPath}");
            File.WriteAllText(ReportPath, log.ToString());
            return;
        }

        var width = GetIntOption(args, "--w", DefaultWidth);
        var height = GetIntOption(args, "--h", DefaultHeight);
        var outPath = GetOption(args, "--out")
                      ?? Path.Combine(Path.GetTempPath(), "ncl-skin-software.png");

        log.AppendLine("NegiCraft Launcher — 软件光栅化皮肤预览");
        log.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine($"皮肤: {skinPath}");
        log.AppendLine($"画布: {width}x{height}");

        var texture = SkinTexture.Decode(File.ReadAllBytes(skinPath));
        if (texture == null)
        {
            log.AppendLine("解码失败 —— 不是能识别的 PNG。");
            File.WriteAllText(ReportPath, log.ToString());
            return;
        }

        var isSlim = texture.IsSlimSkin();
        var format = texture.ResolveFormat(isSlim);
        log.AppendLine($"贴图: {texture.Width}x{texture.Height}  旧格式={texture.IsLegacy}  " +
                       $"判为细臂={isSlim}  →  模型格式={format}");

        var render = new SkinRenderer(texture, width, height, log);
        // 单张正面图：与 GL 版 skinsnap 的默认视角一致，方便叠图对比。
        var front = render.RenderFrame(0f);
        var frontPath = Path.ChangeExtension(outPath, null) + "-front.png";
        File.WriteAllBytes(frontPath, PngCodec.Encode(front));

        // 只出一个角度（诊断用）：--angle 180
        if (float.TryParse(GetOption(args, "--angle"), out var onlyAngle))
        {
            var image = render.RenderFrame(onlyAngle);
            var singlePath = Path.ChangeExtension(outPath, null) + $"-{onlyAngle:0}.png";
            File.WriteAllBytes(singlePath, PngCodec.Encode(image));
            log.AppendLine($"单视角 {onlyAngle} 度: {singlePath}");
        }

        // 四视角拼图：左右镜像、背面剔除、第二层有没有被本体挡住，一眼可判。
        var sheet = render.RenderSheet(ViewAngles);
        File.WriteAllBytes(outPath, PngCodec.Encode(sheet));

        log.AppendLine($"正面: {frontPath}");
        log.AppendLine($"四视角: {outPath}");
        log.AppendLine($"视角角度: {string.Join(", ", ViewAngles)} 度");

        File.WriteAllText(ReportPath, log.ToString());
    }

    /// <summary>把 <see cref="SkinRenderSoftware"/> 包一层，负责"摆姿势 + 渲染 + 拼图"。</summary>
    private sealed class SkinRenderer
    {
        private readonly SkinRenderSoftware _renderer = new();
        private readonly StringBuilder? _report;
        private readonly int _width;
        private readonly int _height;

        public SkinRenderer(SkinTexture texture, int width, int height, StringBuilder? report)
        {
            _width = width;
            _height = height;
            _report = report;

            _renderer.Width = width;
            _renderer.Height = height;
            _renderer.EnableTop = true;
            _renderer.SetSkin(texture);
        }

        public PixelBuffer RenderFrame(float yawDegrees)
        {
            _renderer.ResetPos();

            // 上游的旋转输入是"角度 / 360 当作弧度"，所以这里先换成弧度再乘回 360。
            if (yawDegrees != 0f)
            {
                _renderer.Rot(0f, (float)(yawDegrees * Math.PI / 180.0) * 360f);
            }

            _renderer.Tick(0);
            _renderer.SetPos(0f, 0f);

            var target = new PixelBuffer(_width, _height);
            _renderer.RenderTo(target);

            DumpMatrices(yawDegrees);
            return target;
        }

        /// <summary>把这一帧的矩阵写进报告，视觉不对时用来分辨"矩阵错"还是"UV 错"。</summary>
        private void DumpMatrices(float yawDegrees)
        {
            if (_report == null) return;

            var (model, view, projection, head, leftArm) = _renderer.DebugMatrices();
            _report.AppendLine($"--- 视角 {yawDegrees} 度 ---");
            _report.AppendLine($"  model     = {Row(model.M11, model.M12, model.M13, model.M14)} | {Row(model.M21, model.M22, model.M23, model.M24)} | {Row(model.M31, model.M32, model.M33, model.M34)}");
            _report.AppendLine($"  view      = {Row(view.M11, view.M12, view.M13, view.M14)} | {Row(view.M21, view.M22, view.M23, view.M24)} | {Row(view.M31, view.M32, view.M33, view.M34)}");
            _report.AppendLine($"  proj      = {Row(projection.M11, projection.M12, projection.M13, projection.M14)} | {Row(projection.M21, projection.M22, projection.M23, projection.M24)} | {Row(projection.M31, projection.M32, projection.M33, projection.M34)}");
            _report.AppendLine($"  head      = {Row(head.M41, head.M42, head.M43, head.M44)}");
            _report.AppendLine($"  leftArm   = {Row(leftArm.M41, leftArm.M42, leftArm.M43, leftArm.M44)}");

            static string Row(float a, float b, float c, float d) =>
                $"[{a,8:F4} {b,8:F4} {c,8:F4} {d,8:F4}]";
        }

        public PixelBuffer RenderSheet(IReadOnlyList<float> angles)
        {
            var frame = new PixelBuffer(_width, _height);
            var sheet = new PixelBuffer(_width * angles.Count, _height);

            for (var i = 0; i < angles.Count; i++)
            {
                var single = RenderFrame(angles[i]);
                for (var y = 0; y < _height; y++)
                {
                    Array.Copy(single.Pixels, y * _width, sheet.Pixels, y * sheet.Width + i * _width, _width);
                }
            }

            return sheet;
        }
    }

    /// <summary>
    /// 逐像素比两张图，结果写 <c>%TEMP%\ncl-skin-compare.txt</c>。
    /// 两张图都先合成到白底 —— GL 那边背景是不透明的白，软件这边是透明，
    /// 不合成的话背景差异会把结论淹掉。
    /// </summary>
    private static void Compare(string leftPath, string rightPath)
    {
        var log = new StringBuilder();
        log.AppendLine("NegiCraft Launcher — 皮肤预览逐像素对比");
        log.AppendLine($"{leftPath}");
        log.AppendLine($"{rightPath}");

        var a = Load(leftPath);
        var b = Load(rightPath);
        if (a == null || b == null)
        {
            log.AppendLine("有一张读不出来。");
            File.WriteAllText(ComparePath, log.ToString());
            return;
        }

        if (a.Value.Width != b.Value.Width || a.Value.Height != b.Value.Height)
        {
            log.AppendLine($"尺寸不同: {a.Value.Width}x{a.Value.Height} vs {b.Value.Width}x{b.Value.Height} —— 先对齐尺寸再比。");
            File.WriteAllText(ComparePath, log.ToString());
            return;
        }

        var width = a.Value.Width;
        var height = a.Value.Height;
        var count = width * height;

        long sumR = 0, sumG = 0, sumB = 0;
        int maxR = 0, maxG = 0, maxB = 0;
        var over8 = 0;
        var over32 = 0;

        for (var i = 0; i < count; i++)
        {
            var (ar, ag, ab) = Composite(a.Value.Pixels[i]);
            var (br, bg, bb) = Composite(b.Value.Pixels[i]);

            var dr = Math.Abs(ar - br);
            var dg = Math.Abs(ag - bg);
            var db = Math.Abs(ab - bb);

            sumR += dr; sumG += dg; sumB += db;
            if (dr > maxR) maxR = dr;
            if (dg > maxG) maxG = dg;
            if (db > maxB) maxB = db;

            var worst = Math.Max(dr, Math.Max(dg, db));
            if (worst > 8) over8++;
            if (worst > 32) over32++;
        }

        log.AppendLine($"尺寸: {width}x{height}（{count} 像素，已合成白底）");
        log.AppendLine($"非背景包围盒: A {Bounds(a.Value.Pixels, width, height)}   B {Bounds(b.Value.Pixels, width, height)}");
        log.AppendLine($"平均绝对差: R {sumR / (double)count:F2}  G {sumG / (double)count:F2}  B {sumB / (double)count:F2}");
        log.AppendLine($"最大绝对差: R {maxR}  G {maxG}  B {maxB}");
        log.AppendLine($"差异 >8  的像素: {over8}（{over8 * 100.0 / count:F1}%）");
        log.AppendLine($"差异 >32 的像素: {over32}（{over32 * 100.0 / count:F1}%）");

        ReportAligned(log, a.Value.Pixels, b.Value.Pixels, width, height);

        File.WriteAllText(ComparePath, log.ToString());
        // 模型占的画面范围。两边取景（缩放/位置）不一致时，逐像素差会被边缘差异淹没，
        // 光看百分比分不清"渲染错了"还是"镜头不一样"，所以单独量一下。
        static string Bounds(uint[] pixels, int width, int height)
        {
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var alpha = pixels[y * width + x] >> 24;
                    if (alpha < 16) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            return maxX < 0 ? "(空)" : $"[{minX},{minY}]-[{maxX},{maxY}] = {maxX - minX + 1}x{maxY - minY + 1}";
        }

        static (int R, int G, int B) Composite(uint argb)
        {
            var a = (argb >> 24) & 0xFF;
            if (a == 255)
            {
                return ((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
            }
            // 合成到白底：out = src * a + 255 * (1 - a)
            var r = (int)(((argb >> 16) & 0xFF) * a / 255 + 255 * (255 - a) / 255);
            var g = (int)(((argb >> 8) & 0xFF) * a / 255 + 255 * (255 - a) / 255);
            var bl = (int)((argb & 0xFF) * a / 255 + 255 * (255 - a) / 255);
            return (r, g, bl);
        }

        static (int Width, int Height, uint[] Pixels)? Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var image = PngCodec.Decode(File.ReadAllBytes(path));
                var pixels = new uint[image.Width * image.Height];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var o = i * 4;
                    pixels[i] = ((uint)image.Rgba[o + 3] << 24) | ((uint)image.Rgba[o] << 16) |
                                ((uint)image.Rgba[o + 1] << 8) | image.Rgba[o + 2];
                }

                return (image.Width, image.Height, pixels);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 在 ±24 像素的平移窗口里找出最贴合的位置，再报那个位置上的差异。
    ///
    /// <para>为什么需要：GL 那边的取景由 Avalonia 控件决定（画布尺寸、DPI 缩放、模型偏移），
    /// 跟这边的画布不一定严丝合缝。不对齐的话，光是整体错开十几个像素就能让"差异 &gt;8"
    /// 的比例冲到 25%，把真正的渲染差异淹掉。对齐之后再比，数字才有意义。</para>
    /// </summary>
    private static void ReportAligned(StringBuilder log, uint[] a, uint[] b, int width, int height)
    {
        const int Search = 24;

        var bestDelta = int.MaxValue;
        var bestX = 0;
        var bestY = 0;

        // 步长 2 粗搜一遍就够 —— 这是找整体平移，不是做亚像素配准。
        for (var dy = -Search; dy <= Search; dy += 2)
        {
            for (var dx = -Search; dx <= Search; dx += 2)
            {
                long sum = 0;
                var samples = 0;

                for (var y = Math.Max(0, dy); y < Math.Min(height, height + dy); y += 2)
                {
                    var rowA = y * width;
                    var rowB = (y - dy) * width;

                    for (var x = Math.Max(0, dx); x < Math.Min(width, width + dx); x += 2)
                    {
                        var (ar, ag, ab) = Composite(a[rowA + x]);
                        var (br, bg, bb) = Composite(b[rowB + x - dx]);
                        sum += Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb);
                        samples++;
                    }
                }

                if (samples == 0) continue;
                var delta = (int)(sum / samples);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestX = dx;
                    bestY = dy;
                }
            }
        }

        // 用最佳偏移重算完整统计。
        long sumR = 0, sumG = 0, sumB = 0;
        var over8 = 0;
        var over32 = 0;
        var counted = 0;

        for (var y = Math.Max(0, bestY); y < Math.Min(height, height + bestY); y++)
        {
            var rowA = y * width;
            var rowB = (y - bestY) * width;

            for (var x = Math.Max(0, bestX); x < Math.Min(width, width + bestX); x++)
            {
                var (ar, ag, ab) = Composite(a[rowA + x]);
                var (br, bg, bb) = Composite(b[rowB + x - bestX]);
                var dr = Math.Abs(ar - br);
                var dg = Math.Abs(ag - bg);
                var db = Math.Abs(ab - bb);

                sumR += dr; sumG += dg; sumB += db;
                counted++;

                var worst = Math.Max(dr, Math.Max(dg, db));
                if (worst > 8) over8++;
                if (worst > 32) over32++;
            }
        }

        if (counted == 0)
        {
            log.AppendLine("对齐失败（没有重叠区域）。");
            return;
        }

        log.AppendLine();
        log.AppendLine($"对齐后（B 平移 dx={bestX}, dy={bestY}）:");
        log.AppendLine($"  平均绝对差: R {sumR / (double)counted:F2}  G {sumG / (double)counted:F2}  B {sumB / (double)counted:F2}");
        log.AppendLine($"  差异 >8  的像素: {over8}（{over8 * 100.0 / counted:F1}%）");
        log.AppendLine($"  差异 >32 的像素: {over32}（{over32 * 100.0 / counted:F1}%）");

        static (int R, int G, int B) Composite(uint argb)
        {
            var a = (argb >> 24) & 0xFF;
            if (a == 255)
            {
                return ((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
            }

            var r = (int)(((argb >> 16) & 0xFF) * a / 255 + 255 * (255 - a) / 255);
            var g = (int)(((argb >> 8) & 0xFF) * a / 255 + 255 * (255 - a) / 255);
            var bl = (int)((argb & 0xFF) * a / 255 + 255 * (255 - a) / 255);
            return (r, g, bl);
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int GetIntOption(string[] args, string name, int fallback) =>
        int.TryParse(GetOption(args, name), out var value) ? value : fallback;
}
