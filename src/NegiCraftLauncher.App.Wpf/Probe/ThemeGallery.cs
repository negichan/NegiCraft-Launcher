using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NegiCraftLauncher.Theme.Wpf;

namespace NegiCraftLauncher.App.Wpf.Probe;

/// <summary>
/// P4 的验证入口：把主题画廊摆出来、离屏截一张图，并逐控件检查"样式到底有没有生效"。
///
/// <para>为什么不能只看截图：WPF 的隐式样式没命中时控件会退回系统默认样子 —— 那是"看起来
/// 有点怪"而不是"报错"，截图上一眼不一定认得出来。所以除了出图，还要按名字去模板里
/// <c>FindName</c>，找得到才说明用的是我们这套模板。</para>
///
/// <para>产物：<c>%TEMP%\ncl-wpf-theme.png</c>（截图）与 <c>%TEMP%\ncl-wpf-theme.txt</c>（报告）。</para>
/// </summary>
internal static class ThemeGallery
{
    private static readonly string ShotPath =
        Path.Combine(Path.GetTempPath(), "ncl-wpf-theme.png");

    private static readonly string ReportPath =
        Path.Combine(Path.GetTempPath(), "ncl-wpf-theme.txt");

    public static void Run()
    {
        var log = new StringBuilder();
        log.AppendLine("NegiCraft Launcher — WPF 主题画廊");
        log.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        log.AppendLine();

        var window = new ThemeGalleryWindow
        {
            // 摆到屏幕外。其实 RenderTargetBitmap 是直接渲染视觉树、不看屏幕的，
            // 但摆出去更保险 —— 万一 Show 的那一帧被合成器抓到也不会闪一下。
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowInTaskbar = false,
        };

        try
        {
            window.Show();
            window.UpdateLayout();

            Capture(window, log);
            Audit(window, log);        }
        catch (Exception ex)
        {
            log.AppendLine($"失败: {ex.GetType().Name}: {ex.Message}");
            log.AppendLine(ex.StackTrace);
        }
        finally
        {
            window.Close();
        }

        File.WriteAllText(ReportPath, log.ToString());
    }

    private static void Capture(Window window, StringBuilder log)
    {
        if (window.Content is not FrameworkElement content)
        {
            log.AppendLine("窗口没有内容。");
            return;
        }

        var width = (int)Math.Ceiling(content.ActualWidth);
        var height = (int)Math.Ceiling(content.ActualHeight);
        log.AppendLine($"画廊尺寸: {width}x{height}");

        if (width <= 0 || height <= 0)
        {
            log.AppendLine("布局尺寸为 0 —— 窗口没走到布局。");
            return;
        }

        // 按 2× 光栅化：DIP 布局尺寸完全不变（6px 的进度条还是 6 DIP），只是像素翻倍，
        // 缩略图里那些 4px/6px 的细节才看得出来。1:1 的版本留着对照布局。
        const int Zoom = 2;
        var bitmap = new RenderTargetBitmap(width * Zoom, height * Zoom, 96 * Zoom, 96 * Zoom, PixelFormats.Pbgra32);
        bitmap.Render(content);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(ShotPath))
        {
            encoder.Save(stream);
        }

        log.AppendLine($"截图: {ShotPath}（{width * Zoom}x{height * Zoom}，{Zoom}× 光栅化）");
        log.AppendLine();

        CaptureSections(window, log);
    }

    /// <summary>画廊里每个小节（带 x:Name 的 StackPanel）。</summary>
    private static readonly (string Name, string File)[] Sections =
    [
        ("SecButton", "button"),
        ("SecTextBox", "textbox"),
        ("SecCheckBox", "checkbox"),
        ("SecSlider", "slider"),
        ("SecProgress", "progress"),
        ("SecScroll", "scroll"),
        ("SecMenu", "menu"),
        ("SecItems", "items"),
    ];

    /// <summary>
    /// 逐小节单独出图。整张长图 1014×2602，缩略到能看的时候 4px 的滚动条滑块、
    /// 6px 的进度条全糊成一根线 —— 分块出图才验证得动。
    /// </summary>
    private static void CaptureSections(Window window, StringBuilder log)
    {
        // 1× —— 一个 DIP 一个像素，看图时能按原始尺寸判 4px/6px 的细节。
        // （长图那边用 2× 是因为它整体会被缩略，这里不会。）
        const int Zoom = 1;

        log.AppendLine("=== 分小节截图 ===");

        foreach (var (name, file) in Sections)
        {
            if (window.FindName(name) is not FrameworkElement section) continue;

            var width = (int)Math.Ceiling(section.ActualWidth);
            var height = (int)Math.Ceiling(section.ActualHeight);
            if (width <= 0 || height <= 0) continue;

            var bitmap = RenderAtOrigin(section, width, height, Zoom);
            if (bitmap == null) continue;

            var path = Path.Combine(Path.GetTempPath(), $"ncl-wpf-theme-{file}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
            {
                encoder.Save(stream);
            }

            log.AppendLine($"  {name,-12} → {path}  ({width * Zoom}x{height * Zoom})");
        }

        log.AppendLine();
    }

    /// <summary>
    /// 把一个**子树**渲染到位图左上角。
    ///
    /// <para>不能直接 <c>rtb.Render(section)</c> —— 那样会带上这个元素相对它父级的偏移，
    /// 小节在长图里越靠下，内容就被推到越下面，小尺寸位图里直接整张空白。</para>
    /// </summary>
    private static RenderTargetBitmap? RenderAtOrigin(FrameworkElement element, int width, int height, int zoom)
    {
        var brush = new VisualBrush(element)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width * zoom, height * zoom, 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>
    /// 按模板里的名字去 <c>FindName</c>，验证每个控件用的确实是我们的模板而不是系统默认的。
    /// 顺带把从样式拿到的值（圆角、水印、尺寸）打出来，方便和 Avalonia 版对着看。
    /// </summary>
    private static void Audit(Window window, StringBuilder log)
    {
        log.AppendLine("=== 样式命中检查（模板里找得到名字 = 用的是我们这套模板）===");

        var found = new List<string>();

        Walk(window, element =>
        {
            switch (element)
            {
                case Button button:
                    found.Add(Check(button, "Button", "Root") +
                              $"  CornerRadius={Negi.GetCornerRadius(button)}" +
                              $"  尺寸={Fmt(button)}");
                    break;

                case TextBox box:
                    found.Add(Check(box, "TextBox", "PART_ContentHost") +
                              $"  水印=\"{Negi.GetPlaceholder(box)}\"" +
                              $"  CornerRadius={Negi.GetCornerRadius(box)}" +
                              $"  尺寸={Fmt(box)}");
                    break;

                case CheckBox check:
                    found.Add(Check(check, "CheckBox(开关)", "Track") +
                              $"  IsChecked={check.IsChecked}" +
                              $"  尺寸={Fmt(check)}");
                    break;

                case Slider slider:
                    var thumb = slider.Template?.FindName("Thumb", slider) as FrameworkElement;
                    found.Add(Check(slider, "Slider", "PART_Track") +
                              $"  Value={slider.Value}" +
                              $"  尺寸={Fmt(slider)}" +
                              $"  滑块位置={ThumbOffset(slider, thumb)}");
                    break;

                case ProgressBar bar:
                    var indicator = bar.Template?.FindName("PART_Indicator", bar);
                    found.Add($"{Name(bar)}  PART_Track={(bar.Template?.FindName("PART_Track", bar) != null)}" +
                              $"  PART_Indicator={(indicator is FrameworkElement fe ? Fmt(fe) : "缺失")}" +
                              $"  Value={bar.Value}");
                    break;

                case ScrollBar scrollBar:
                    var styleOwner = scrollBar.Style?.TargetType.Name ?? "(无显式样式)";
                    var parent = VisualTreeHelper.GetParent(scrollBar) as FrameworkElement;
                    found.Add(Check(scrollBar, $"ScrollBar({scrollBar.Orientation})", "PART_Track") +
                              $"  Width={scrollBar.Width}  MinWidth={scrollBar.MinWidth}" +
                              $"  ActualWidth={scrollBar.ActualWidth:F0}  尺寸={Fmt(scrollBar)}" +
                              $"  样式目标={styleOwner}" +
                              $"  父级={parent?.GetType().Name}/{parent?.ActualWidth:F0}");
                    break;

                case ScrollViewer viewer:
                    found.Add(Check(viewer, "ScrollViewer", "PART_ScrollContentPresenter") +
                              $"  ScrollableHeight={viewer.ScrollableHeight:F0}");
                    break;

                case MenuItem item:
                    found.Add(Check(item, "MenuItem", "PART_LayoutRoot") +
                              $"  Header=\"{item.Header}\"  Role={item.Role}");
                    break;

                case Separator separator:
                    found.Add($"<Separator>  尺寸={Fmt(separator)}" +
                              $"  有模板={separator.Template != null}" +
                              $"  背景={(separator.Background as SolidColorBrush)?.Color.ToString() ?? "(无)"}");
                    break;

                case ToolTip tip:
                    found.Add(Check(tip, "ToolTip", "PART_ContentPresenter") +
                              $"  Content=\"{tip.Content}\"");
                    break;
            }
        });

        foreach (var line in found) log.AppendLine("  " + line);

        // ToolTip 没法放进画廊（WPF 不允许它有父级），只能从资源侧验证隐式样式在不在。
        log.AppendLine();
        log.AppendLine("=== ToolTip 样式（无法内联展示，只看资源）===");
        // 注意：不要试图去 ControlTemplate.VisualTree 里找元素 —— BAML 编译过的模板在
        // 加载时就被优化掉了，VisualTree 拿不到工厂树。只能看 Setter 有没有。
        if (Application.Current.TryFindResource(typeof(ToolTip)) is Style toolTipStyle)
        {
            var setters = toolTipStyle.Setters.OfType<Setter>().ToList();
            log.AppendLine($"  命中隐式样式，TargetType={toolTipStyle.TargetType.Name}");
            foreach (var property in new[] { "Background", "Foreground", "Padding", "MaxWidth", "Template" })
            {
                var setter = setters.FirstOrDefault(s => s.Property.Name == property);
                log.AppendLine($"    {property} = {(setter == null ? "(未设)" : setter.Value is ControlTemplate ? "有模板" : setter.Value)}");
            }
        }
        else
        {
            log.AppendLine("  没找到 ToolTip 的隐式样式 —— 主题没合并进来？");
        }

        // ContextMenu 同理：它是弹窗，截图里不会有，只能查资源。
        log.AppendLine();
        log.AppendLine("=== ContextMenu 样式（弹窗，截图里不会有）===");
        log.AppendLine(Application.Current.TryFindResource(typeof(ContextMenu)) is Style
            ? "  命中隐式样式"
            : "  没找到隐式样式");

        log.AppendLine();
        log.AppendLine("=== 主题资源 ===");
        foreach (var key in new[]
                 {
                     "AppBackgroundBrush", "CardBackgroundBrush", "AccentBrush", "BorderLineBrush",
                     "RingBrush", "ForegroundBrush", "MutedForegroundBrush", "PrimaryBrush",
                     "PrimaryForegroundBrush", "MutedBrush",
                 })
        {
            var value = Application.Current.TryFindResource(key);
            log.AppendLine($"  {key} = {(value is SolidColorBrush b ? b.Color.ToString() : value?.ToString() ?? "(缺)")}");
        }
    }

    private static string Check(Control control, string label, string templatePartName) =>
        $"{Name(control)}  {label}  {templatePartName}={(control.Template?.FindName(templatePartName, control) != null)}";

    private static string Name(Control control) =>
        string.IsNullOrEmpty(control.Name) ? $"<{control.GetType().Name}>" : control.Name;

    private static string Fmt(FrameworkElement element) =>
        $"{element.ActualWidth:F0}x{element.ActualHeight:F0}";

    /// <summary>滑块中心相对轨道左边缘的位置 —— 用数字确认值真的映射到了位置上。</summary>
    private static string ThumbOffset(Slider slider, FrameworkElement? thumb)
    {
        if (thumb == null) return "(没有 Thumb)";
        if (slider.Template?.FindName("PART_Track", slider) is not FrameworkElement track) return "(没有轨道)";

        try
        {
            var center = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, 0), track);
            return $"{center.X:F0}/{track.ActualWidth:F0}";
        }
        catch (InvalidOperationException)
        {
            return "(不在同一棵视觉树里)";
        }
    }

    private static void Walk(DependencyObject root, Action<DependencyObject> visit)
    {
        visit(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            Walk(VisualTreeHelper.GetChild(root, i), visit);
        }
    }
}
