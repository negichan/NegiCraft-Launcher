using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NegiCraftLauncher.App;
using NegiCraftLauncher.App.Services;
using NegiCraftLauncher.Theme;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Probe;

internal static class UiPerformance
{
    public static void Run(string[] args) => _ = RunSequence(args);

    private static async Task RunSequence(string[] args)
    {
        MainWindow? window = null;
        var exitCode = 0;
        try
        {
            var vm = new MainWindowViewModel(new WpfBitmapDecoder());
            window = new MainWindow { DataContext = vm, ShowActivated = false };
            window.Show();
            await Task.Delay(3000);
            // 页面入场动画由 MotionEngine.Enabled 统一开关，而它默认读系统的"显示动画"设置。
            // 测动画开销时必须把它强制打开，否则量到的是"瞬时切换"——动画压根没跑，
            // 基线和改后都是同一个数字，对比没有意义。
            if (args.Contains("--force-motion")) MotionEngine.Override = true;
            if (args.Contains("--no-effects")) Walk(window, element => element.Effect = null);
            if (args.Contains("--no-blur")) vm.BgBlur = 0;
            if (args.Contains("--no-skin-model")) vm.HomeSkinModelVisible = false;
            // 整块背景藏掉：若瓶颈是"每帧要画的像素太多"，帧率会立刻上去。
            if (args.Contains("--no-background"))
            {
                if (window.FindName("BgRoot") is UIElement bg) bg.Visibility = Visibility.Collapsed;
            }
            // 缩小窗口：若是"填充率不够"（每帧要重画的像素太多），帧率会随面积走。
            var smallIndex = Array.IndexOf(args, "--window-size");
            if (smallIndex >= 0 && smallIndex + 1 < args.Length)
            {
                var parts = args[smallIndex + 1].Split('x');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                {
                    window.Width = w;
                    window.Height = h;
                    await Task.Delay(500);
                }
            }
            if (args.Contains("--no-static-cache"))
            {
                foreach (var name in new[] { "BgRoot", "SidebarBackdrop", "ShellShadowLayer" })
                    ((UIElement)window.FindName(name)).CacheMode = null;
            }
            var results = new List<object>();
            results.Add(await Measure("home", window, async () => await Task.Delay(2200)));
            results.Add(await Measure("popovers", window, async () =>
            {
                for (var i = 0; i < 6; i++)
                {
                    vm.IsInstPopOpen = true;
                    await Task.Delay(220);
                    vm.IsInstPopOpen = false;
                    await Task.Delay(160);
                }
            }));
            var pageSwitchTimes = new List<(string Page, double Ms)>();
            var tweensBeforePages = MotionEngine.StartedCount;
            results.Add(await Measure("pages", window, async () =>
            {
                foreach (var page in new[] { "instances", "download", "settings", "home", "instances", "home" })
                {
                    var stamp = Stopwatch.GetTimestamp();
                    vm.GoCommand.Execute(page);
                    pageSwitchTimes.Add((page, Stopwatch.GetElapsedTime(stamp).TotalMilliseconds));
                    await Task.Delay(350);
                }
            }));
            var tweensDuringPages = MotionEngine.StartedCount - tweensBeforePages;

            // 页面切换的位移到底有没有发生：从渲染变换里读实际偏移。
            // 这是"确定性"的那一半 —— fps 看不出来，位移读数看得见。
            var pageOffsets = new List<(string Page, double Start, double Peak)>();
            foreach (var page in new[] { "instances", "download", "home" })
            {
                vm.GoCommand.Execute(page == "instances" ? "home" : "instances");
                await Task.Delay(300);
                var element = (FrameworkElement)window.FindName(
                    page == "home" ? "PageHome" : page == "instances" ? "PageInstances" : "PageDownload");
                vm.GoCommand.Execute(page);
                var start = 0.0;
                var peak = 0.0;
                for (var frame = 0; frame < 12; frame++)
                {
                    await Task.Delay(16);
                    var transform = element.RenderTransform as System.Windows.Media.TranslateTransform;
                    if (transform is null) break;
                    var y = transform.Y;
                    if (frame == 0) start = y;
                    peak = Math.Max(peak, Math.Abs(y));
                }
                pageOffsets.Add((page, start, peak));
            }

            var settingsOpenTimes = new List<double>();
            results.Add(await Measure("settings-page", window, async () =>
            {
                // 设置现在是整页模式，"打开"就是一次换页：看的是换页本身的开销，
                // 连着量四次，取的是稳定态（第一次还带页面入场动画的冷启动）。
                for (var i = 0; i < 4; i++)
                {
                    var stamp = Stopwatch.GetTimestamp();
                    vm.GoCommand.Execute("settings");
                    settingsOpenTimes.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
                    await Task.Delay(320);
                    vm.GoCommand.Execute("home");
                    await Task.Delay(280);
                }
            }));
            var pressedKey = (DependencyPropertyKey)typeof(ButtonBase)
                .GetField("IsPressedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var button = (ButtonBase)window.FindName("InstSelectBtn");
            results.Add(await Measure("button-presses", window, async () =>
            {
                for (var i = 0; i < 12; i++)
                {
                    button.SetValue(pressedKey, true);
                    await Task.Delay(75);
                    button.SetValue(pressedKey, false);
                    await Task.Delay(115);
                }
            }));
            results.Add(await Measure("other-popovers", window, async () =>
            {
                foreach (var (page, property) in new[]
                {
                    ("home", nameof(MainWindowViewModel.IsAccPopOpen)),
                    ("instances", nameof(MainWindowViewModel.IsLoaderPopOpen)),
                    ("instances", nameof(MainWindowViewModel.IsFolderPopOpen)),
                    ("download", nameof(MainWindowViewModel.IsDlPopOpen)),
                    ("home", nameof(MainWindowViewModel.IsDialogOpen)),
                    ("home", nameof(MainWindowViewModel.IsPackImportOpen)),
                    ("home", nameof(MainWindowViewModel.IsAddFolderOpen)),
                    ("home", nameof(MainWindowViewModel.IsClosePromptOpen)),
                    // 两个悬停提示条走 Pop 的 Tip 模式（无锚定定位、无缩放），单独覆盖一遍
                    ("home", nameof(MainWindowViewModel.IsVolumePopOpen)),
                    ("home", nameof(MainWindowViewModel.IsAccountInfoOpen)),
                })
                {
                    vm.GoCommand.Execute(page);
                    await Task.Delay(250);
                    var toggle = typeof(MainWindowViewModel).GetProperty(property)!;
                    toggle.SetValue(vm, true);
                    await Task.Delay(250);
                    toggle.SetValue(vm, false);
                    await Task.Delay(160);
                }
            }));
            var sample = typeof(MainWindow).GetMethod("UpdateWindowButtonTone", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var sampleTimes = new List<double>();
            for (var i = 0; i < 12; i++)
            {
                var stamp = Stopwatch.GetTimestamp();
                sample.Invoke(window, null);
                sampleTimes.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
                await Task.Delay(20);
            }
            var report = new
            {
                RenderingTier = RenderCapability.Tier >> 16,
                Dpi = VisualTreeHelper.GetDpi(window).DpiScaleX,
                WindowSize = new[] { window.ActualWidth, window.ActualHeight },
                Background = vm.IsVideoBackground ? "video" : vm.IsCustomBackground ? "image" : "pixel-art",
                MotionEnabled = MotionEngine.Enabled,
                DiagnosticOptions = args.Where(arg => arg.StartsWith("--no-") || arg == "--force-motion").ToArray(),
                StaticBackgroundToneTimerRunning = ((DispatcherTimer)typeof(MainWindow)
                    .GetField("_bgToneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).IsEnabled,
                Note = "Render callbacks measure UI frame pacing, not independently measured displayed FPS.",
                Scenarios = results,
                PageSwitchMs = pageSwitchTimes.Select(s => new { s.Page, Ms = Math.Round(s.Ms, 1) }).ToArray(),
                TweensStartedDuringPages = tweensDuringPages,
                PageTransitionOffsets = pageOffsets.Select(o => new
                {
                    o.Page,
                    StartOffset = Math.Round(o.Start, 1),
                    PeakOffset = Math.Round(o.Peak, 1),
                }).ToArray(),
                SettingsOpenMs = Summarize(settingsOpenTimes),
                RepeatedToneUpdateMs = Summarize(sampleTimes),
            };
            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            var index = Array.IndexOf(args, "--report");
            var path = index >= 0 && index + 1 < args.Length
                ? Path.GetFullPath(args[index + 1])
                : Path.Combine(Path.GetTempPath(), "ncl-ui-performance.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            Console.WriteLine(json);
            Console.WriteLine($"Report: {path}");
        }
        catch (Exception ex)
        {
            exitCode = 1;
            Console.WriteLine(ex);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ncl-ui-performance-error.txt"), ex.ToString());
        }
        finally
        {
            Application.Current.Shutdown(exitCode);
        }
    }

    private static void Walk(DependencyObject node, Action<UIElement> action)
    {
        if (node is UIElement element) action(element);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Walk(VisualTreeHelper.GetChild(node, i), action);
    }

    private static async Task<object> Measure(string name, MainWindow window, Func<Task> activity)
    {
        var gaps = new List<double>();
        var inputDelays = new List<double>();
        TimeSpan? lastFrame = null;
        var frames = 0;
        var cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        void Frame(object? sender, EventArgs args)
        {
            var time = ((RenderingEventArgs)args).RenderingTime;
            if (lastFrame == time) return;
            if (lastFrame is { } previous) gaps.Add((time - previous).TotalMilliseconds);
            lastFrame = time;
            frames++;
        }
        CompositionTarget.Rendering += Frame;
        using var cancel = new CancellationTokenSource();
        var input = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(20);
                if (cancel.IsCancellationRequested) break;
                var stamp = Stopwatch.GetTimestamp();
                await window.Dispatcher.InvokeAsync(
                    () => inputDelays.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds),
                    DispatcherPriority.Input);
            }
        });
        try
        {
            await activity();
        }
        finally
        {
            CompositionTarget.Rendering -= Frame;
            cancel.Cancel();
            await input;
        }
        return new
        {
            Name = name,
            RenderCallbacksPerSecond = frames / watch.Elapsed.TotalSeconds,
            FrameGapMs = Summarize(gaps),
            InputDispatchMs = Summarize(inputDelays),
            CpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds,
        };
    }

    private static object Summarize(List<double> values)
    {
        values.Sort();
        return new
        {
            Samples = values.Count,
            Mean = values.Count == 0 ? 0 : values.Average(),
            P95 = values.Count == 0 ? 0 : values[(int)Math.Ceiling(values.Count * 0.95) - 1],
            Max = values.Count == 0 ? 0 : values[^1],
            Over33Ms = values.Count(v => v > 33),
        };
    }
}
