using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.App;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.App.Media;
using NegiCraftLauncher.App.Services;
using NegiCraftLauncher.Core;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.ViewModels;
using Windows.Media.MediaProperties;
using Windows.Storage;

internal static class Program
{
    private static int _passed;
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppDispatcher.Current = new WpfUiDispatcher(app.Dispatcher);
        var frame = new DispatcherFrame();
        var run = Run(args);
        _ = run.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try
        {
            run.GetAwaiter().GetResult();
            Console.WriteLine($"RESULT: {_passed}/{_passed} passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static void Check(string name, bool valid)
    {
        if (!valid) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS {name}");
        _passed++;
    }

    private static async Task Run(string[] args)
    {
        var output = Path.GetFullPath(Option(args, "--output", "bin/verification/background-ui"));
        Directory.CreateDirectory(output);
        var source = Option(args, "--video", "");
        var quality = Option(args, "--quality", "efficient");
        var cache = new BackgroundVideoCache(Path.Combine(output, "cache"));
        if (!string.IsNullOrEmpty(source))
        {
            var originalSize = new FileInfo(source).Length;
            var originalTime = File.GetLastWriteTimeUtc(source);
            var prepareWatch = Stopwatch.StartNew();
            var prepared = await cache.PrepareAsync(source, quality, null, CancellationToken.None);
            Console.WriteLine($"Prepared {prepared} ({prepareWatch.Elapsed.TotalSeconds:F1}s)");
            Check("preparing preserves the original file",
                new FileInfo(source).Length == originalSize && File.GetLastWriteTimeUtc(source) == originalTime);
            if (quality != "original")
            {
                var file = await StorageFile.GetFileFromPathAsync(prepared);
                var profile = await MediaEncodingProfile.CreateFromFileAsync(file);
                var inputProfile = await MediaEncodingProfile.CreateFromFileAsync(
                    await StorageFile.GetFileFromPathAsync(source));
                Check("playback copies preserve the presence of source audio",
                    (profile.Audio is null) == (inputProfile.Audio is null));
                Check("playback copy bounds resolution and frame rate",
                    Math.Max(profile.Video.Width, profile.Video.Height) <= (quality == "clear" ? 1920 : 1280) &&
                    profile.Video.FrameRate.Numerator / (double)profile.Video.FrameRate.Denominator <= 30.01);
                Check("encoding keeps aspect ratio without upscaling",
                    profile.Video.Width <= inputProfile.Video.Width &&
                    profile.Video.Height <= inputProfile.Video.Height &&
                    Math.Abs(profile.Video.Width / (double)profile.Video.Height -
                        inputProfile.Video.Width / (double)inputProfile.Video.Height) < 0.02);
                var lastWrite = File.GetLastWriteTimeUtc(prepared);
                var reused = await cache.PrepareAsync(source, quality, null, CancellationToken.None);
                Check("a second import reuses the same completed copy", reused == prepared);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                try
                {
                    await cache.PrepareAsync(source, "clear", null, canceled.Token);
                    Check("cancelled preparations stop", false);
                }
                catch (OperationCanceledException) { Check("cancelled preparations stop", true); }
                Check("cancelled preparations leave no partial output",
                    !Directory.EnumerateFiles(Path.Combine(output, "cache"), "*.partial.mp4").Any());
                if (args.Contains("--cancel-during-encoding"))
                {
                    var cancelCacheDirectory = Path.Combine(output, "cancel-cache");
                    var cancelCache = new BackgroundVideoCache(cancelCacheDirectory);
                    using var duringEncoding = new CancellationTokenSource();
                    var started = new TaskCompletionSource();
                    var progress = new Progress<double>(_ =>
                    {
                        started.TrySetResult();
                        duringEncoding.Cancel();
                    });
                    var encoding = cancelCache.PrepareAsync(source, "clear", progress, duringEncoding.Token);
                    await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    try
                    {
                        await encoding;
                        Check("in-flight encoding can be cancelled", false);
                    }
                    catch (OperationCanceledException) { Check("in-flight encoding can be cancelled", true); }
                    Check("in-flight cancellation releases and removes encoder output",
                        !Directory.EnumerateFiles(cancelCacheDirectory, "*.mp4").Any());
                }
            }
        }
        if (args.Contains("--cache-only")) return;
        var maintenance = Path.Combine(output, "maintenance-cache");
        Directory.CreateDirectory(maintenance);
        var maintenanceSource = Path.Combine(output, "maintenance-source.mp4");
        File.WriteAllText(maintenanceSource, "source fixture");
        var maintenanceCache = new BackgroundVideoCache(maintenance);
        var copy = maintenanceCache.CachePath(maintenanceSource, "efficient");
        var other = Path.Combine(maintenance, "unrelated.mp4");
        File.WriteAllText(copy, "playback copy fixture");
        File.WriteAllText(other, "unrelated fixture");
        await maintenanceCache.RemoveAsync(maintenanceSource);
        Check("cache removal deletes only the selected source's managed copies",
            !File.Exists(copy) && File.Exists(other) && File.Exists(maintenanceSource));

        var vm = new MainWindowViewModel(new WpfBitmapDecoder());
        // Redirect saves before changing any UI state; never overwrite the user's settings.
        var launcher = (Launcher)typeof(MainWindowViewModel).GetField("_launcher", Private)!.GetValue(vm)!;
        var settingsPath = Path.Combine(output, "settings.json");
        typeof(LauncherSettings).GetProperty(nameof(LauncherSettings.FilePath))!.SetValue(launcher.Settings, settingsPath);
        typeof(MainWindowViewModel).GetField("_applyingSettings", Private)!.SetValue(vm, true);
        vm.ResetBackgroundCommand.Execute(null);
        vm.BackgroundLibrary.Clear();
        vm.BackgroundVideoQuality = quality;
        var window = new MainWindow
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
            DataContext = vm,
        };
        window.Show();
        await Task.Delay(200);
        try
        {
            var image = Path.Combine(output, "wallpaper.png");
            MakeImage(image);
            vm.CurrentPage = "settings";
            vm.CurrentSettingsTab = "外观";
            vm.SetBackgroundCommand.Execute(image);
            await Task.Delay(300);
            Check("importing leaves the settings page in place", vm.CurrentPage == "settings");
            Check("image import creates one active library entry",
                vm.BackgroundLibrary.Count == 1 && vm.BackgroundLibrary[0].IsSelected);
            var tuning = window.BackgroundTuningWindowForDebug!;
            Check("tuning opens with the shared view model", tuning is not null && tuning.DataContext == vm);
            Check("the tool window close button has a visible reachable icon",
                HitsButton(tuning!, (Button)tuning!.FindName("CloseTuningButton")) &&
                ((Button)tuning.FindName("CloseTuningButton")).Padding == new Thickness(0));
            vm.BgBlur = 17;
            vm.BgBrightness = -33;
            vm.BgZoom = 160;
            vm.BgContrast = 12;
            Check("blur updates the actual background effect",
                ((FrameworkElement)window.FindName("BgLayer")).Effect is System.Windows.Media.Effects.BlurEffect { Radius: 17 });
            Invoke((Button)tuning!.FindName("ResetTuningButton"));
            await Task.Delay(100);
            Check("the real reset button restores adjustments and keeps the image",
                vm.BgBlur == 0 && vm.BgBrightness == 0 && vm.BgZoom == 100 && vm.BgContrast == 0 &&
                vm.CustomBackgroundPath == image);
            Check("zero blur releases the offscreen effect",
                ((FrameworkElement)window.FindName("BgLayer")).Effect is null);
            var done = (Button)tuning.FindName("DoneTuningButton");
            Check("done and reset are reachable hit targets", HitsButton(tuning, done) &&
                HitsButton(tuning, (Button)tuning.FindName("ResetTuningButton")));
            var closedHandle = new WindowInteropHelper(tuning).Handle;
            tuning.Top = SystemParameters.WorkArea.Bottom - tuning.ActualHeight;
            var beforeHeight = tuning.ActualHeight;
            vm.IsPreparingBackgroundVideo = true;
            await Task.Delay(80);
            Check("expanding adjustment controls keeps the bottom buttons on screen",
                tuning.ActualHeight > beforeHeight &&
                tuning.Top + tuning.ActualHeight <= SystemParameters.WorkArea.Bottom + 1);
            vm.IsPreparingBackgroundVideo = false;
            Invoke(done);
            await WaitFor(() => !vm.IsBgTuningOpen && window.BackgroundTuningWindowForDebug is null,
                TimeSpan.FromSeconds(5));
            Check("done destroys the tool window and clears its state",
                !vm.IsBgTuningOpen && window.BackgroundTuningWindowForDebug is null && !IsWindow(closedHandle));
            window.Hide();
            window.Show();
            await Task.Delay(120);
            Check("returning to the owner cannot resurrect a completed tuning window",
                !vm.IsBgTuningOpen && !IsWindow(closedHandle) && window.BackgroundTuningWindowForDebug is null);
            vm.OpenBgTuningCommand.Execute(null);
            await Task.Delay(100);
            var reopened = window.BackgroundTuningWindowForDebug!;
            reopened.Close();
            Check("closing the native tool window also exits framing mode",
                !vm.IsBgTuningOpen && !vm.IsFramingVisible);
            vm.SetBackgroundCommand.Execute(image);
            vm.CloseBgTuningCommand.Execute(null);
            Check("importing the same file does not duplicate library entries", vm.BackgroundLibrary.Count == 1);
            var asset = vm.BackgroundLibrary[0];
            vm.ResetBackgroundCommand.Execute(null);
            Check("reset wallpaper preserves the saved library", vm.BackgroundLibrary.Count == 1 && !asset.IsSelected);
            vm.UseSavedBackgroundCommand.Execute(asset);
            Check("saved backgrounds can be reapplied without opening adjustment mode",
                vm.CustomBackgroundPath == image && asset.IsSelected && !vm.IsBgTuningOpen);
            var missing = new BackgroundAssetModel { Path = image + ".missing", IsVideo = false };
            vm.BackgroundLibrary.Add(missing);
            vm.UseSavedBackgroundCommand.Execute(missing);
            Check("a missing saved file leaves the current background intact", vm.CustomBackgroundPath == image);
            vm.RemoveSavedBackgroundCommand.Execute(asset);
            Check("removing the current item resets playback and preserves its source",
                !vm.HasCustomBackground && File.Exists(image) && !vm.BackgroundLibrary.Contains(asset));
            vm.RemoveSavedBackgroundCommand.Execute(missing);

            if (!string.IsNullOrEmpty(source))
            {
                vm.SetVideoBackgroundCommand.Execute(source);
                vm.CloseBgTuningCommand.Execute(null);
                await WaitFor(() => !vm.IsPreparingBackgroundVideo, TimeSpan.FromMinutes(3));
                Check("background preparation completes without UI errors", !vm.HasBackgroundVideoError);
                await WaitFor(() => window.VideoBackgroundDebug.Contains("pos=") &&
                    Position(window.VideoBackgroundDebug) > 0.2, TimeSpan.FromSeconds(15));
                var beforeDrag = Position(window.VideoBackgroundDebug);
                window.BeginDragPerfHold();
                await Task.Delay(650);
                Check("video continues playing during window drag",
                    Position(window.VideoBackgroundDebug) > beforeDrag + 0.3);
                window.EndDragPerfHold();
                vm.CurrentPage = "home";
                await Task.Delay(150);
                Shot(window, Path.Combine(output, "video-home.png"));
                var sidebar = (FrameworkElement)window.FindName("SidebarVideo");
                Check("sidebar blur processes only a sidebar-sized surface", sidebar.ActualWidth <= 80);
                var tone = typeof(MainWindow).GetMethod("UpdateWindowButtonTone", Private)!;
                var watch = Stopwatch.StartNew();
                for (var i = 0; i < 20; i++) tone.Invoke(window, null);
                Check("video button contrast avoids expensive render readback", watch.Elapsed.TotalMilliseconds < 30);
                var report = await Measure(window, quality);
                File.WriteAllText(Path.Combine(output, "performance.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine(JsonSerializer.Serialize(report));
                window.WindowState = WindowState.Minimized;
                await Task.Delay(250);
                var paused = Position(window.VideoBackgroundDebug);
                await Task.Delay(500);
                Check("minimizing pauses background decoding", Math.Abs(Position(window.VideoBackgroundDebug) - paused) < 0.15);
                window.WindowState = WindowState.Normal;
                await Task.Delay(500);
                Check("restoring resumes background playback", Position(window.VideoBackgroundDebug) > paused + 0.2);
                vm.CurrentPage = "settings";
                await Task.Delay(100);
                Shot(window, Path.Combine(output, "video-settings.png"));
                vm.OpenBgTuningCommand.Execute(null);
                await Task.Delay(100);
                Shot(window.BackgroundTuningWindowForDebug!, Path.Combine(output, "video-tuning.png"));
                vm.CloseBgTuningCommand.Execute(null);
                // Exercise latest-wins cancellation and immediate switch to a still image.
                vm.BackgroundVideoQuality = quality == "clear" ? "efficient" : "clear";
                vm.SetBackgroundCommand.Execute(image);
                vm.CloseBgTuningCommand.Execute(null);
                await Task.Delay(300);
                Check("switching to an image cancels pending video preparation",
                    !vm.IsPreparingBackgroundVideo && !vm.HasBackgroundVideoError && !vm.IsVideoBackground &&
                    !window.VideoBackgroundDebug.Contains(Path.GetFileName(source)));
            }

            var broken = Path.Combine(output, "broken.mp4");
            File.WriteAllText(broken, "invalid media fixture");
            vm.BackgroundVideoQuality = "efficient";
            vm.SetVideoBackgroundCommand.Execute(broken);
            vm.CloseBgTuningCommand.Execute(null);
            await WaitFor(() => !vm.IsPreparingBackgroundVideo, TimeSpan.FromSeconds(15));
            Check("bad videos expose an error and remain in the library for retry or removal",
                vm.HasBackgroundVideoError && vm.BackgroundLibrary.Any(item => item.Path == broken));
            vm.SetBackgroundCommand.Execute(image);
            vm.CloseBgTuningCommand.Execute(null);
            Check("a subsequent image clears the failed video state", !vm.HasBackgroundVideoError);

            typeof(MainWindowViewModel).GetField("_applyingSettings", Private)!.SetValue(vm, false);
            vm.BgBrightness = 3;
            await Task.Delay(650);
            var saved = LauncherSettings.Load(settingsPath);
            Check("library and video quality survive settings serialization",
                saved.BackgroundLibrary.Count == vm.BackgroundLibrary.Count &&
                saved.BackgroundVideoQuality == vm.BackgroundVideoQuality);
            await CheckGallery(window, vm, image, source, args, output);
            window.Width = 1080;
            window.Height = 740;
            vm.CurrentPage = "settings";
            await Task.Delay(100);
            Shot(window, Path.Combine(output, "minimum-settings.png"));
            if (args.Contains("--interactive") && !string.IsNullOrEmpty(source))
            {
                vm.BackgroundVideoQuality = "efficient";
                vm.SetVideoBackgroundCommand.Execute(source);
                vm.CloseBgTuningCommand.Execute(null);
                await WaitFor(() => !vm.IsPreparingBackgroundVideo, TimeSpan.FromMinutes(3));
                window.Title = "NegiCraft Background Preview";
                window.Left = 160;
                window.Top = 100;
                window.Width = 1250;
                window.Height = 800;
                window.ShowInTaskbar = true;
                Console.WriteLine("Interactive preview ready; close this test window to finish.");
                var finished = new TaskCompletionSource();
                window.Closed += (_, _) => finished.TrySetResult();
                await finished.Task;
            }
        }
        finally { window.ExitApplication(); }
    }

    private static double Position(string state)
    {
        var text = state.Split("pos=")[1].Split('s')[0];
        return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task CheckGallery(MainWindow window, MainWindowViewModel vm,
        string fixture, string video, string[] args, string output)
    {
        var image = Option(args, "--image", fixture);
        var poster = await BackgroundThumbnailCache.GetAsync(image, false);
        Check("image posters are frozen and bounded to 384 pixels",
            poster is { IsFrozen: true } && Math.Max(poster.PixelWidth, poster.PixelHeight) <= 384);
        var repeated = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => BackgroundThumbnailCache.GetAsync(image, false)));
        Check("repeated poster requests share the cached bitmap",
            repeated.All(item => ReferenceEquals(item, poster)));
        Check("missing media returns a fallback without failing the UI",
            await BackgroundThumbnailCache.GetAsync(image + ".missing", false) is null);
        var invalid = Path.Combine(output, "not-an-image.png");
        File.WriteAllText(invalid, "invalid image");
        Check("invalid images return a fallback without failing the UI",
            await BackgroundThumbnailCache.GetAsync(invalid, false) is null);
        var mutable = Path.Combine(output, "mutable-poster.png");
        MakeImage(mutable, 96, 64);
        var before = await BackgroundThumbnailCache.GetAsync(mutable, false);
        MakeImage(mutable, 64, 512);
        File.SetLastWriteTimeUtc(mutable, DateTime.UtcNow.AddSeconds(2));
        var after = await BackgroundThumbnailCache.GetAsync(mutable, false);
        Check("changed files invalidate posters and portrait images retain their aspect ratio",
            before is not null && after is not null && !ReferenceEquals(before, after) &&
            after.PixelHeight <= 384 && Math.Abs(after.PixelWidth / (double)after.PixelHeight - .125) < .02);
        using (File.Open(mutable, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check("completed thumbnail decoding releases the original file", true);
        if (!string.IsNullOrEmpty(video))
        {
            var frame = await BackgroundThumbnailCache.GetAsync(video, true);
            Check("the real video has a frozen, bounded still preview",
                frame is { IsFrozen: true } && Math.Max(frame.PixelWidth, frame.PixelHeight) <= 384);
        }

        vm.ResetBackgroundCommand.Execute(null);
        vm.BackgroundLibrary.Clear();
        vm.SetBackgroundCommand.Execute(image);
        vm.CloseBgTuningCommand.Execute(null);
        var imageAsset = vm.BackgroundLibrary.Single();
        Check("the current background summary follows the active library item",
            ReferenceEquals(vm.CurrentBackground, imageAsset));
        if (!string.IsNullOrEmpty(video))
            vm.BackgroundLibrary.Add(new BackgroundAssetModel { Path = video, IsVideo = true, IsAvailable = true });
        var extraVideo = Option(args, "--extra-video", "");
        if (!string.IsNullOrEmpty(extraVideo))
            vm.BackgroundLibrary.Add(new BackgroundAssetModel { Path = extraVideo, IsVideo = true, IsAvailable = true });
        vm.CurrentPage = "settings";
        vm.CurrentSettingsTab = "外观";
        window.UpdateLayout();
        var section = Descendants<FrameworkElement>(window).Single(item => item.Name == "BackgroundSection");
        var scroll = (ScrollViewer)window.FindName("PageSettingsScroll");
        section.BringIntoView();
        await Task.Delay(200);
        var thumbnails = Descendants<BackgroundThumbnail>(section).ToArray();
        await WaitFor(() => thumbnails.All(item => !item.IsLoading), TimeSpan.FromSeconds(30));
        Check("current wallpaper and all saved media show real preview images",
            thumbnails.Length >= vm.BackgroundLibrary.Count + 1 && thumbnails.All(item => item.HasThumbnail));
        Shot(window, Path.Combine(output, "gallery-image.png"));

        var apply = Descendants<Button>(section).Single(item =>
            item.Name == "ApplyBackgroundTile" && ReferenceEquals(item.DataContext, imageAsset));
        apply.BringIntoView();
        await Task.Delay(100);
        Check("the thumbnail is a reachable full-size selection target",
            apply.ActualWidth >= 180 && apply.ActualHeight >= 100 && HitsButton(window, apply));
        Invoke(apply);
        Check("clicking the current thumbnail keeps the tuning window closed", !vm.IsBgTuningOpen);

        if (!string.IsNullOrEmpty(video))
        {
            var videoAsset = vm.BackgroundLibrary.First(item => item.IsVideo);
            var videoButton = Descendants<Button>(section).Single(item =>
                item.Name == "ApplyBackgroundTile" && ReferenceEquals(item.DataContext, videoAsset));
            Invoke(videoButton);
            await WaitFor(() => ReferenceEquals(vm.CurrentBackground, videoAsset) &&
                !vm.IsPreparingBackgroundVideo, TimeSpan.FromMinutes(3));
            Check("clicking a video preview updates the current wallpaper without opening tuning",
                ReferenceEquals(vm.CurrentBackground, videoAsset) && videoAsset.IsSelected &&
                !imageAsset.IsSelected && !vm.IsBgTuningOpen && !vm.HasBackgroundVideoError);
            section.BringIntoView();
            await Task.Delay(200);
            Shot(window, Path.Combine(output, "gallery-video.png"));
        }

        foreach (var width in new[] { 1080.0, 1500.0 })
        {
            window.Width = width;
            window.Height = width == 1080 ? 740 : 900;
            window.UpdateLayout();
            section.BringIntoView();
            await Task.Delay(150);
            var buttons = Descendants<Button>(section).Where(item => item.Name == "ApplyBackgroundTile").ToArray();
            Check($"wallpaper previews stay within the settings column at window width {width}",
                buttons.All(item =>
                {
                    var point = item.TranslatePoint(new Point(), section);
                    return point.X >= 0 && point.X + item.ActualWidth <= section.ActualWidth + 1;
                }) && scroll.ExtentWidth <= scroll.ViewportWidth + 1);
            Shot(window, Path.Combine(output, $"gallery-{width}.png"));
        }

        var removed = vm.BackgroundLibrary.First();
        var remove = Descendants<Button>(section).Single(item =>
            item.Name == "RemoveBackgroundTile" && ReferenceEquals(item.DataContext, removed));
        remove.BringIntoView();
        await Task.Delay(100);
        Check("remove is separate from the preview selection button", HitsButton(window, remove));
        Invoke(remove);
        await WaitFor(() => !vm.BackgroundLibrary.Contains(removed), TimeSpan.FromSeconds(5));
        Check("removing from the gallery preserves the original media",
            !vm.BackgroundLibrary.Contains(removed) && File.Exists(removed.Path));
        vm.ResetBackgroundCommand.Execute(null);
        Check("restoring the default wallpaper clears the current summary", vm.CurrentBackground is null);
        vm.BackgroundLibrary.Clear();
        window.UpdateLayout();
        section.BringIntoView();
        await Task.Delay(100);
        Shot(window, Path.Combine(output, "gallery-empty.png"));
        Check("an empty gallery shows a default wallpaper and no stale thumbnails",
            !vm.HasCustomBackground && !Descendants<BackgroundThumbnail>(section).Any(item => item.IsVisible || item.HasThumbnail));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("Background state did not settle.");
            await Task.Delay(100);
        }
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static bool HitsButton(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
        for (var node = window.InputHitTest(point) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, button)) return button.IsEnabled && button.IsVisible;
        return false;
    }

    private static void MakeImage(string path, int width = 240, int height = 146)
    {
        var pixels = Enumerable.Repeat((byte)165, width * height * 4).ToArray();
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static void Shot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static async Task<object> Measure(MainWindow window, string quality)
    {
        var process = Process.GetCurrentProcess();
        GetProcessIoCounters(process.Handle, out var startIo);
        var cpuStart = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var delays = new List<double>();
        // Dispatch from a worker so a busy UI cannot hide its own stalls.
        var input = Task.Run(async () =>
        {
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(50);
                var stamp = Stopwatch.GetTimestamp();
                await window.Dispatcher.InvokeAsync(
                    () => delays.Add(Stopwatch.GetElapsedTime(stamp).TotalMilliseconds),
                    DispatcherPriority.Input);
            }
        });
        await input;
        process.Refresh();
        GetProcessIoCounters(process.Handle, out var endIo);
        var seconds = watch.Elapsed.TotalSeconds;
        delays.Sort();
        return new
        {
            Quality = quality,
            WorkingSetMiB = process.WorkingSet64 / 1048576.0,
            PrivateMiB = process.PrivateMemorySize64 / 1048576.0,
            ReadMiBPerSecond = (endIo.ReadTransferCount - startIo.ReadTransferCount) / 1048576.0 / seconds,
            WriteMiBPerSecond = (endIo.WriteTransferCount - startIo.WriteTransferCount) / 1048576.0 / seconds,
            CpuMillisecondsPerSecond = (process.TotalProcessorTime - cpuStart).TotalMilliseconds / seconds,
            InputDelayP95Ms = delays[(int)(delays.Count * .95)],
            InputDelayMaxMs = delays[^1],
            Video = window.VideoBackgroundDebug,
            Note = "Process I/O includes cached reads; dispatch delays do not measure displayed FPS.",
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr handle);
}
