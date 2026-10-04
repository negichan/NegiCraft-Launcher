using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using MinecraftSkinRender;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;
using NegiCraftLauncher.Skin.Rendering;

namespace NegiCraftLauncher.App.Probe;

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
            Compare(left, GetOption(args, "--with") ?? "",
                Array.IndexOf(args, "--sample") >= 0);
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

        // --gpu-notop：把第二层关掉。**两边都要关** —— 只关 GPU 侧的话，
        // 软件图上看到的"头"其实是帽子层（u 正好差 +0.5），两张图比的不是同一个东西。
        var noTop = Array.IndexOf(args, "--gpu-notop") >= 0;

        var render = new SkinRenderer(texture, width, height, log, noTop);
        // 单张正面图：与 GL 版 skinsnap 的默认视角一致，方便叠图对比。
        var front = render.RenderFrame(0f);
        var frontPath = Path.ChangeExtension(outPath, null) + "-front.png";
        File.WriteAllBytes(frontPath, PngCodec.Encode(front));

        // --gpu：同一帧再走一遍 Viewport3D 硬件路径，出一张尺寸完全相同的图，
        // 之后用 --compare 逐像素对，验证绕序 / 取景 / UV / alpha 与软件后端一致。
        if (Array.IndexOf(args, "--gpu") >= 0)
        {
            var suffix = "-gpu" + (Array.IndexOf(args, "--gpu-notop") >= 0 ? "notop" : "");
            var gpuFrontPath = Path.ChangeExtension(outPath, null) + suffix + ".png";
            var gpu = new GpuRenderer(texture, width, height, args);
            var (gpuWidth, gpuHeight) = gpu.RenderFrame(0f, gpuFrontPath, log);
            gpu.Close();
            log.AppendLine($"GPU 正面: {gpuFrontPath}  实际抓到 {gpuWidth}x{gpuHeight}");

            // 抓屏拿的是物理像素，DPI 缩放下与画布 DIP 不等 —— 软件那张图按同一尺寸重出一遍，
            // 否则 Compare 会因为尺寸不同直接拒比。
            if (gpuWidth > 0 && gpuHeight > 0 && (gpuWidth != width || gpuHeight != height))
            {
                var rescale = new SkinRenderer(texture, gpuWidth, gpuHeight, null, noTop);
                File.WriteAllBytes(frontPath, PngCodec.Encode(rescale.RenderFrame(0f)));
                log.AppendLine($"软件正面已按 {gpuWidth}x{gpuHeight} 重出，与 GPU 图同尺寸。");
            }
        }

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

        public SkinRenderer(SkinTexture texture, int width, int height, StringBuilder? report, bool noTop = false)
        {
            _width = width;
            _height = height;
            _report = report;

            _renderer.Width = width;
            _renderer.Height = height;
            _renderer.SetSkin(texture);
            if (noTop) _renderer.EnableTop = false;
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
    /// GPU 后端（WPF <c>Viewport3D</c>）的出图。姿势序列与 <see cref="SkinRenderer"/> 逐行一致 ——
    /// 两边喂给 <c>SkinRenderBase</c> 的东西必须完全相同，比出来的差异才只反映"渲染"这一层。
    ///
    /// <para><b>为什么要开真窗口抓屏，而不是 <c>RenderTargetBitmap</c></b>：RTB 走的是 WPF 的
    /// <b>软件</b>渲染管线，而 3D 内容只在硬件管线里出 —— 抓出来恒为全透明。
    /// 这一点由 <c>--gpu3d</c> 探针实测确认过。</para>
    ///
    /// <para><b>为什么是白底</b>：抓屏拿不到 alpha（窗口已经跟桌面合成过了）。
    /// 软件那张图是透明底，而 <see cref="Compare"/> 会把两边都合成到白底 ——
    /// 所以这边给窗口铺白底，比出来才只反映模型本身的差异。</para>
    /// </summary>
    private sealed class GpuRenderer
    {
        private readonly SkinRenderGpu _renderer = new();
        private readonly SkinGpuViewport _viewport = new();
        private readonly Window _window;
        private readonly int _width;
        private readonly int _height;

        /// <summary>背景铺洋红，抓屏后按这个色扣成透明 —— 否则抓屏全是 A=255，
        /// 软件图与 GPU 图的"非背景包围盒"没法比。</summary>
        private static readonly Color KeyColor = Colors.Magenta;

        public GpuRenderer(SkinTexture texture, int width, int height, string[] args)
        {
            _width = width;
            _height = height;

            _renderer.Width = width;
            _renderer.Height = height;
            _renderer.EnableTop = true;
            _renderer.SetSkin(texture);
            // 要在 SetSkin 之后 —— SetSkin 会按皮肤类型重算 EnableTop。
            if (Array.IndexOf(args, "--gpu-notop") >= 0) _renderer.EnableTop = false;

            _viewport.View.Width = width;
            _viewport.View.Height = height;

            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = new SolidColorBrush(KeyColor),
                Width = width,
                Height = height,
                Left = 40,
                Top = 40,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = _viewport.View,
            };
            _window.Show();
        }

        /// <summary>抓一帧存盘，返回<b>实际抓到的像素尺寸</b>（DPI 缩放下会与画布 DIP 不等）。</summary>
        public (int Width, int Height) RenderFrame(float yawDegrees, string outPath, StringBuilder? log)
        {
            _renderer.ResetPos();

            if (yawDegrees != 0f)
            {
                _renderer.Rot(0f, (float)(yawDegrees * Math.PI / 180.0) * 360f);
            }

            _renderer.Tick(0);
            _renderer.SetPos(0f, 0f);

            _viewport.Sync(_renderer);

            DumpCameraDiagnostics(log);

            // 等合成器真的把这一帧推出去。
            PumpFrames(8);

            var hwnd = new WindowInteropHelper(_window).Handle;
            if (!Win32.GetWindowRect(hwnd, out var rect))
            {
                return (0, 0);
            }

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return (0, 0);

            var screen = Win32.CaptureScreen(rect.Left, rect.Top, width, height);

            var target = new PixelBuffer(width, height);
            for (var i = 0; i < screen.Length; i++)
            {
                // 抓屏没有 alpha：洋红底扣成全透明，其余补成不透明。
                // A=255 时预乘等于直通，可以直接放。
                var c = screen[i] & 0x00FFFFFFu;
                target.Pixels[i] = c == 0x00FF00FFu ? 0u : (0xFF000000u | c);
            }

            File.WriteAllBytes(outPath, PngCodec.Encode(target));
            return (width, height);
        }

        /// <summary>
        /// 把库给的相机矩阵和 WPF 自己算的投影矩阵并排打出来。
        ///
        /// <para>目的：确认 <c>System.Numerics.Matrix4x4</c> 和 WPF <c>Matrix3D</c> 是不是同一套约定
        /// （行主序 + 行向量 + 平移在第 4 行）。如果 WPF 的 <c>PerspectiveCamera</c> 算出来的投影矩阵
        /// 与库的 <c>CreatePerspectiveFieldOfView</c> 结果只差一个"视场角取水平还是垂直"，
        /// 那 <c>MatrixCamera</c> 就是能用的，出图糊掉就得往别处找。</para>
        /// </summary>
        private void DumpCameraDiagnostics(StringBuilder? log)
        {
            if (log == null) return;

            var proj = _renderer.MatrixOf(ModelPartType.Proj);
            var view = _renderer.MatrixOf(ModelPartType.View);

            log.AppendLine("--- GPU 相机诊断 ---");
            log.AppendLine($"  库 proj   = {M(proj)}");
            log.AppendLine($"  库 view   = {M(view)}");

            // 库的相机是写死的：eye=(0,0,7) 看向原点、垂直 fov=45°、near 0.1 / far 10。
            var wpf = new PerspectiveCamera(new Point3D(0, 0, 7), new Vector3D(0, 0, -1),
                new Vector3D(0, 1, 0), 45)
            {
                NearPlaneDistance = 0.1,
                FarPlaneDistance = 10,
            };
            var wpfProj = ProjectionMatrixOf(wpf, _width / (double)_height);
            log.AppendLine($"  WPF proj  = {M3(wpfProj)}  （fov=45 按 WPF 自己的解释）");

            static Matrix3D ProjectionMatrixOf(ProjectionCamera camera, double aspect)
            {
                // WPF 把 GetProjectionMatrix 藏成 internal —— 探针用反射拿，只为打印对比。
                var method = typeof(ProjectionCamera).GetMethod(
                    "GetProjectionMatrix",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                return method?.Invoke(camera, [aspect]) is Matrix3D m ? m : Matrix3D.Identity;
            }

            static string M(System.Numerics.Matrix4x4 m) =>
                $"r1[{m.M11,8:F4} {m.M12,8:F4} {m.M13,8:F4} {m.M14,8:F4}] " +
                $"r2[{m.M21,8:F4} {m.M22,8:F4} {m.M23,8:F4} {m.M24,8:F4}] " +
                $"r3[{m.M31,8:F4} {m.M32,8:F4} {m.M33,8:F4} {m.M34,8:F4}] " +
                $"r4[{m.M41,8:F4} {m.M42,8:F4} {m.M43,8:F4} {m.M44,8:F4}]";

            static string M3(Matrix3D m) =>
                $"r1[{m.M11,8:F4} {m.M12,8:F4} {m.M13,8:F4} {m.M14,8:F4}] " +
                $"r2[{m.M21,8:F4} {m.M22,8:F4} {m.M23,8:F4} {m.M24,8:F4}] " +
                $"r3[{m.M31,8:F4} {m.M32,8:F4} {m.M33,8:F4} {m.M34,8:F4}] " +
                $"r4[{m.OffsetX,8:F4} {m.OffsetY,8:F4} {m.OffsetZ,8:F4} {m.M44,8:F4}]";
        }

        public void Close() => _window.Close();

        /// <summary>把消息泵转起来等 N 帧 —— 不开窗 / 不泵的话 WPF 根本不会渲染。</summary>
        private static void PumpFrames(int count)
        {
            var frame = new DispatcherFrame();
            var seen = 0;

            EventHandler onRender = (_, _) =>
            {
                if (++seen >= count) frame.Continue = false;
            };

            CompositionTarget.Rendering += onRender;
            try
            {
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                CompositionTarget.Rendering -= onRender;
            }
        }
    }

    /// <summary>
    /// 逐像素比两张图，结果写 <c>%TEMP%\ncl-skin-compare.txt</c>。
    /// 两张图都先合成到白底 —— GL 那边背景是不透明的白，软件这边是透明，
    /// 不合成的话背景差异会把结论淹掉。
    /// </summary>
    private static void Compare(string leftPath, string rightPath, bool sample)
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

        // --sample：在模型范围内打一张 10x10 的采样网格，两个图并排打 (R,G)。
        // 配 UV 标定贴图（R=u、G=v）用时，这两列直接就是两个后端各自采到的 (u,v)*255。
        if (sample) ReportGrid(log, a.Value.Pixels, b.Value.Pixels, width, height);

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

    /// <summary>
    /// 在 A（软件图）的非透明包围盒上打一张 10x10 采样网格，两图并排打 <c>RRGG</c>。
    ///
    /// <para>配 UV 标定贴图（<c>R=u</c>、<c>G=v</c>）时，这两列就是两个后端各自采到的
    /// <c>(u,v)*255</c> —— 一眼就能看出 WPF 到底把纹理坐标映射成了什么。</para>
    /// </summary>
    private static void ReportGrid(StringBuilder log, uint[] a, uint[] b, int width, int height)
    {
        const int steps = 10;

        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if ((a[(y * width) + x] >> 24) < 16) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
        {
            log.AppendLine("采样网格: A 是空的。");
            return;
        }

        log.AppendLine();
        log.AppendLine($"采样网格（A 包围盒 [{minX},{minY}]-[{maxX},{maxY}]，格式 软件/GPU 的 RRGG）:");

        for (var gy = 0; gy < steps; gy++)
        {
            var row = new StringBuilder($"  y{gy}  ");
            for (var gx = 0; gx < steps; gx++)
            {
                var x = minX + ((maxX - minX) * gx / (steps - 1));
                var y = minY + ((maxY - minY) * gy / (steps - 1));
                row.Append($" {Rg(a[(y * width) + x])}/{Rg(b[(y * width) + x])}");
            }

            log.AppendLine(row.ToString());
        }

        static string Rg(uint c) => $"{((c >> 16) & 0xFF):X2}{((c >> 8) & 0xFF):X2}";
    }

    private static string? GetOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static int GetIntOption(string[] args, string name, int fallback) =>
        int.TryParse(GetOption(args, name), out var value) ? value : fallback;
}
