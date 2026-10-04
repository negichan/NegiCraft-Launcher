using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace NegiCraftLauncher.App.Wpf.Probe;

/// <summary>
/// WPF 3D 在这台机器上"材质颜色上不去"的定位探针。
///
/// <para><b>已经排掉的</b>：<c>RenderTargetBitmap</c> 抓不到 3D（走软件管线）；
/// 多 <c>Viewport3D</c> 并排互相干扰；正面绕序是索引 <c>0,1,2</c>；
/// <b>分层透明窗不是元凶</b>（不透明窗表现一模一样）。</para>
///
/// <para><b>上一轮剩下的线索</b>（四个三角形只差材质）：</para>
/// <list type="bullet">
/// <item>Emissive + 冻结纯色 → <b>整个三角形没画出来</b></item>
/// <item>Emissive + 未冻纯色 → 纯白</item>
/// <item>Emissive + 冻结贴图 → 纯白</item>
/// <item>Diffuse + 环境光 + 冻结纯色 → <b>颜色正确</b></item>
/// </list>
/// <para>看着像 <c>EmissiveMaterial</c> 这条路径整个不可用。但桌宠要的是<b>贴图</b>，
/// 所以这一轮把 8 个组合一次问完，重点押在
/// <c>DiffuseMaterial + ImageBrush + AmbientLight</c> 能不能出纹理。</para>
///
/// <para><b>布局</b>：<c>PerspectiveCamera.FieldOfView</c> 是<b>水平</b>视场角。
/// 窗口 720x540 DIP（宽高比 1.333）、相机 z=10、fov=45 ⇒ z=0 处半宽 4.142、半高 3.107。
/// 四个三角形中心 x = -3/-1/1/3、半宽 0.85，正好一格一个且都在视锥内。
/// 两排分别是 y∈[0.6,2.4] 与 y∈[-2.4,-0.6]。</para>
/// </summary>
internal static class Gpu3dProbe
{
    private static readonly StringBuilder Log = new();

    private const int Width = 720;
    private const int Height = 540;

    /// <summary>窗口物理尺寸换算到 DIP 的基准（抓屏拿的是物理像素，按比例切片就不怕 DPI）。</summary>
    private static readonly double[] ColumnCenters = [-3.0, -1.0, 1.0, 3.0];

    private static Window? _window;
    private static int _frames;
    private static string _reportPath = "";
    private static string _shotPath = "";

    private enum Kind
    {
        DiffuseSolid,
        DiffuseTexture,
        DiffuseTextureLive,
        DiffuseTextureTiled,
        EmissiveSolidFrozen,
        EmissiveSolidLive,
        EmissiveTextureFrozen,
    }

    private sealed record Spec(string Label, Color Color, Kind Kind);

    /// <summary>上排 1-4，下排 5-8。</summary>
    private static readonly Spec[][] Rows =
    [
        [
            new("Diffuse + 冻结纯色(红) + 环境光", Colors.Red, Kind.DiffuseSolid),
            new("Diffuse + 冻结贴图(棋盘) + 环境光", Colors.White, Kind.DiffuseTexture),
            new("Emissive + 冻结纯色(青)", Colors.Cyan, Kind.EmissiveSolidFrozen),
            new("Emissive + 冻结贴图(棋盘)", Colors.White, Kind.EmissiveTextureFrozen),
        ],
        [
            new("Diffuse + 未冻贴图(棋盘) + 环境光", Colors.White, Kind.DiffuseTextureLive),
            new("Diffuse + 冻结贴图(Stretch=Fill) + 环境光", Colors.White, Kind.DiffuseTextureTiled),
            new("Diffuse + 冻结纯色(蓝) + 环境光", Colors.Blue, Kind.DiffuseSolid),
            new("Emissive + 未冻纯色(橙)", Colors.Orange, Kind.EmissiveSolidLive),
        ],
    ];

    public static void Run(string[] args)
    {
        var opaque = Array.IndexOf(args, "--opaque") >= 0;
        var tag = opaque ? "opaque" : "layered";

        _reportPath = Path.Combine(Path.GetTempPath(), $"ncl-gpu3d-{tag}.txt");
        _shotPath = Path.Combine(Path.GetTempPath(), $"ncl-gpu3d-{tag}-shot.png");

        Log.Clear();
        Log.AppendLine($"WPF 3D 探针 —— 窗口={(opaque ? "不透明" : "分层透明")} / 8 个材质组合");
        Log.AppendLine($"渲染层级 Tier={RenderCapability.Tier >> 16}  " +
                       $"进程渲染模式={RenderOptions.ProcessRenderMode}");
        Log.AppendLine();

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = !opaque,
            Background = Brushes.Magenta,
            Width = Width,
            Height = Height,
            Left = 40,
            Top = 40,
            Topmost = true,
            ShowInTaskbar = false,
            Content = BuildContent(),
        };

        _window.Show();
        CompositionTarget.Rendering += OnFrame;
    }

    private static Grid BuildContent()
    {
        var group = new Model3DGroup();

        for (var row = 0; row < Rows.Length; row++)
        {
            for (var col = 0; col < Rows[row].Length; col++)
            {
                group.Children.Add(Triangle(ColumnCenters[col], row, Rows[row][col]));
            }
        }

        group.Children.Add(new AmbientLight(Colors.White));

        var viewport = new Viewport3D
        {
            Camera = new PerspectiveCamera(
                new Point3D(0, 0, 10), new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), 45),
        };
        viewport.Children.Add(new ModelVisual3D { Content = group });

        // 2D 对照：最左上角一小块，出红色就说明窗口合成本身没问题。
        // 用 Border 而不是 Rectangle —— 后者在 System.Windows.Shapes 里，
        // 那个命名空间一引进来 `Path` 就和 System.IO.Path 撞名（CS0104）。
        var control = new Border
        {
            Width = 20,
            Height = 16,
            Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 2, 0, 0),
        };

        var grid = new Grid();
        grid.Children.Add(viewport);
        grid.Children.Add(control);
        return grid;
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        if (++_frames < 12) return;
        CompositionTarget.Rendering -= OnFrame;

        try
        {
            CaptureFromScreen();
        }
        catch (Exception ex)
        {
            Log.AppendLine($"屏幕抓取异常: {ex}");
        }

        File.WriteAllText(_reportPath, Log.ToString());
        _window?.Close();
        Application.Current.Shutdown();
    }

    private static void CaptureFromScreen()
    {
        if (_window is null) return;

        var hwnd = new WindowInteropHelper(_window).Handle;
        var exStyle = Win32.GetWindowLong(hwnd, -20);
        Log.AppendLine($"窗口样式 WS_EX_LAYERED={(exStyle & 0x00080000) != 0}  " +
                       $"AllowsTransparency={_window.AllowsTransparency}");

        if (!Win32.GetWindowRect(hwnd, out var rect))
        {
            Log.AppendLine("GetWindowRect 失败。");
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        Log.AppendLine($"窗口 {width}x{height}（DIP {_window.Width}x{_window.Height}）");

        var pixels = Win32.CaptureScreen(rect.Left, rect.Top, width, height);

        var shot = new Raster.PixelBuffer(width, height);
        for (var i = 0; i < pixels.Length; i++) shot.Pixels[i] = 0xFF000000u | pixels[i];
        File.WriteAllBytes(_shotPath, Raster.PngCodec.Encode(shot));
        Log.AppendLine($"截图: {_shotPath}");
        Log.AppendLine();

        Log.AppendLine($"0 2D 红方块对照   {Sample(pixels, width, height, 0.016, 0.018)}");
        Log.AppendLine();

        // 列区间按世界坐标算：x 映射到屏幕的 [0,1] 是 (x + 4.142) / 8.284。
        double[] edges = [-0.02, 0.25, 0.50, 0.75, 1.02];
        (double From, double To)[] rowBands = [(0.10, 0.42), (0.58, 0.90)];

        for (var row = 0; row < Rows.Length; row++)
        {
            for (var col = 0; col < Rows[row].Length; col++)
            {
                var index = (row * 4) + col + 1;
                var spec = Rows[row][col];
                var text = Describe(pixels, width, height,
                    edges[col], edges[col + 1], rowBands[row].From, rowBands[row].To);
                Log.AppendLine($"{index} {spec.Label,-34} {text}");
            }
            Log.AppendLine();
        }
    }

    private static string Sample(uint[] pixels, int width, int height, double fx, double fy)
    {
        var x = Math.Clamp((int)(fx * width), 0, width - 1);
        var y = Math.Clamp((int)(fy * height), 0, height - 1);
        return $"({x},{y}) #{Win32.At(pixels, width, x, y):X6}";
    }

    /// <summary>数一格里的非洋红像素并报主色。不能按预期颜色去数 —— 渲染成白的就全落空了。</summary>
    private static string Describe(
        uint[] pixels, int width, int height, double fromFx, double toFx, double fromFy, double toFy)
    {
        var fromX = Math.Clamp((int)(fromFx * width), 0, width);
        var toX = Math.Clamp((int)(toFx * width), fromX, width);
        var fromY = Math.Clamp((int)(fromFy * height), 0, height);
        var toY = Math.Clamp((int)(toFy * height), fromY, height);

        var histogram = new Dictionary<uint, int>();
        var total = 0;

        for (var y = fromY; y < toY; y++)
        {
            for (var x = fromX; x < toX; x++)
            {
                var c = Win32.At(pixels, width, x, y);
                var r = (int)((c >> 16) & 0xFF);
                var g = (int)((c >> 8) & 0xFF);
                var b = (int)(c & 0xFF);
                if (r > 170 && b > 170 && g < 90) continue; // 洋红底色

                total++;
                histogram[c] = histogram.TryGetValue(c, out var n) ? n + 1 : 1;
            }
        }

        if (total == 0) return "空白（整格都是底色）";

        var best = 0u;
        var bestCount = 0;
        foreach (var (color, count) in histogram)
        {
            if (count <= bestCount) continue;
            best = color;
            bestCount = count;
        }

        return $"{total} 像素非底色, 主色 #{best:X6}（占 {bestCount}）";
    }

    private static GeometryModel3D Triangle(double centerX, int row, Spec spec)
    {
        const double halfWidth = 0.85;
        const double span = 0.9;   // 半高
        var centerY = row == 0 ? 1.5 : -1.5;

        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(centerX - halfWidth, centerY - span, 0),
                new(centerX + halfWidth, centerY - span, 0),
                new(centerX, centerY + span, 0),
            },
            TextureCoordinates = new PointCollection
            {
                new(0, 1),
                new(1, 1),
                new(0.5, 0),
            },
            TriangleIndices = new Int32Collection([0, 1, 2]),
        };
        mesh.Freeze();

        // 注意：这里**不能**写 `Material material;` —— `Material.Icons.WPF` 让全局命名空间里
        // 多了一个 `Material` 命名空间，而命名空间查找优先于 using 指令，
        // 于是 `Material` 被解析成命名空间、报 CS0118。走 GeometryModel3D.Material 属性绕开。
        var model = new GeometryModel3D { Geometry = mesh };
        model.Material = BuildMaterial(spec);
        return model;
    }

    private static System.Windows.Media.Media3D.Material BuildMaterial(Spec spec)
    {
        var brush = new SolidColorBrush(spec.Color);

        switch (spec.Kind)
        {
            case Kind.DiffuseSolid:
                return Freeze(new DiffuseMaterial(brush));

            case Kind.DiffuseTexture:
                return Freeze(new DiffuseMaterial(Freeze(new ImageBrush(BuildChecker()))));

            case Kind.DiffuseTextureLive:
                return new DiffuseMaterial(new ImageBrush(BuildChecker()));

            case Kind.DiffuseTextureTiled:
            {
                var image = new ImageBrush(BuildChecker())
                {
                    Stretch = Stretch.Fill,
                    TileMode = TileMode.None,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center,
                };
                return Freeze(new DiffuseMaterial(Freeze(image)));
            }

            case Kind.EmissiveSolidFrozen:
                return Freeze(new EmissiveMaterial(brush));

            case Kind.EmissiveSolidLive:
                return new EmissiveMaterial(new SolidColorBrush(spec.Color));

            default:
                return Freeze(new EmissiveMaterial(Freeze(new ImageBrush(BuildChecker()))));
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>64x64 的青色/黄色棋盘，当贴图用。</summary>
    private static BitmapSource BuildChecker()
    {
        const int size = 64;
        const int block = 16;
        var stride = size * 4;
        var data = new byte[stride * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var cyan = ((x / block) + (y / block)) % 2 == 0;
                var o = (y * stride) + (x * 4);
                data[o] = cyan ? (byte)255 : (byte)0;      // B
                data[o + 1] = 255;                          // G
                data[o + 2] = cyan ? (byte)0 : (byte)255;   // R
                data[o + 3] = 255;                          // A
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, data, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
