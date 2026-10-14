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
            if (args.Contains("--no-effects")) Walk(window, element => element.Effect = null);
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
            results.Add(await Measure("pages", window, async () =>
            {
                foreach (var page in new[] { "instances", "download", "settings", "home", "instances", "home" })
                {
                    vm.GoCommand.Execute(page);
                    await Task.Delay(350);
                }
            }));
            var settingsOpenTimes = new List<double>();
            results.Add(await Measure("settings-dialog", window, async () =>
            {
                for (var i = 0; i < 4; i++)
                {
                    var stamp = Stopwatch.GetTimestamp();
                    window.OpenSettingsDialog();
                    settingsOpenTimes.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds);
                    await Task.Delay(320);
                    window.IsSettingsOpen = false;
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
                DiagnosticOptions = args.Where(arg => arg.StartsWith("--no-")).ToArray(),
                StaticBackgroundToneTimerRunning = ((DispatcherTimer)typeof(MainWindow)
                    .GetField("_bgToneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).IsEnabled,
                Note = "Render callbacks measure UI frame pacing, not independently measured displayed FPS.",
                Scenarios = results,
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
