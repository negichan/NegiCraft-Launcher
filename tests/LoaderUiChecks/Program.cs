using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.App;
using NegiCraftLauncher.App.Services;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;

internal static class Program
{
    private static int _passed;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "bin/verification/loader-ui");
        Directory.CreateDirectory(output);
        var app = new App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        AppDispatcher.Current = new WpfUiDispatcher(app.Dispatcher);
        var frame = new DispatcherFrame();
        var run = Run(output);
        _ = run.ContinueWith(_ => app.Dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        try
        {
            run.GetAwaiter().GetResult();
            Console.WriteLine($"RESULT: {_passed}/{_passed} passed");
            return 0;
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static void Check(string name, bool valid)
    {
        if (!valid) throw new InvalidOperationException(name);
        Console.WriteLine($"PASS {name}");
        _passed++;
    }

    private static async Task Run(string output)
    {
        var vm = new MainWindowViewModel(new WpfBitmapDecoder());
        var window = new MainWindow
        {
            DataContext = vm, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000,
        };
        window.Show();
        await Task.Delay(150);
        var game = new GameVersionModel
        {
            Id = "1.21.1", Kind = "release", KindLabel = "release",
            ReleasedAt = DateTimeOffset.UnixEpoch, ReleaseText = "", JavaText = "Java 21",
            Icon = PixelArt.CreateBlockCube("grass", 40),
        };

        vm.OpenInstallSheetCommand.Execute(game);
        var forge = vm.InstallLoaders.First(loader => loader.Kind == "forge");
        var fabric = vm.InstallLoaders.First(loader => loader.Kind == "fabric");
        var first = vm.SelectInstallLoaderCommand.ExecuteAsync(forge);
        Check("loader buttons remain enabled while a request is pending",
            vm.SelectInstallLoaderCommand.CanExecute(fabric));
        var second = vm.SelectInstallLoaderCommand.ExecuteAsync(fabric);
        Check("a rapid second click selects the new loader immediately", ReferenceEquals(vm.InstallLoader, fabric));
        vm.CloseInstallSheetCommand.Execute(null);
        vm.OpenInstallSheetCommand.Execute(game);
        var reopened = vm.SelectInstallLoaderCommand.ExecuteAsync(forge);
        vm.CloseInstallSheetCommand.Execute(null);
        await Task.WhenAll(first, second, reopened).WaitAsync(TimeSpan.FromSeconds(5));
        Check("closed sheets stop their loading indicator", !vm.IsLoadingLoaderVersions);
        vm.OpenInstallSheetCommand.Execute(game);
        Check("cancelled requests do not populate a reopened sheet",
            vm.InstallLoader is null && vm.InstallLoaderVersions.Count == 0 && !vm.HasLoaderVersionsError);
        await Task.Delay(450);

        vm.InstallLoader = fabric;
        vm.InstallIconBitmap = fabric.Icon;
        vm.InstallSummary = "Minecraft 1.21.1 · Fabric";
        foreach (var loader in vm.InstallLoaders) loader.IsSelected = ReferenceEquals(loader, fabric);
        vm.IsLoadingLoaderVersions = true;
        vm.LoaderVersionsHint = "正在获取 Fabric 版本…";
        await Task.Delay(180);
        var spinner = (FrameworkElement)window.FindName("LoaderVersionsSpinner");
        var skeleton = (FrameworkElement)window.FindName("LoaderVersionsSkeleton");
        var angle = ((RotateTransform)spinner.RenderTransform).Angle;
        var opacity = skeleton.Opacity;
        Shot(window, Path.Combine(output, "loading-a.png"));
        await Task.Delay(220);
        Check("the loading spinner rotates", Math.Abs(((RotateTransform)spinner.RenderTransform).Angle - angle) > 10);
        Check("the loading placeholders pulse", Math.Abs(skeleton.Opacity - opacity) > 0.02);
        Shot(window, Path.Combine(output, "loading-b.png"));

        vm.IsLoadingLoaderVersions = false;
        vm.HasLoaderVersionsError = true;
        vm.LoaderVersionsHint = "暂时无法获取版本，请重试";
        await Task.Delay(80);
        Check("hidden loading indicators release their animation clocks",
            !((RotateTransform)spinner.RenderTransform).HasAnimatedProperties && !skeleton.HasAnimatedProperties);
        Shot(window, Path.Combine(output, "error.png"));
        vm.HasLoaderVersionsError = false;
        vm.LoaderVersionsHint = "";
        var versions = Enumerable.Range(1, 60)
            .Select(i => new LoaderVersionModel { Version = $"0.16.{61 - i}" }).ToArray();
        vm.InstallLoaderVersions = versions;
        await Task.Delay(160);
        var list = (ListBox)window.FindName("InstallLoaderVersionList");
        var realized = versions.Select((_, i) => list.ItemContainerGenerator.ContainerFromIndex(i))
            .Count(container => container is not null);
        Console.WriteLine($"Realized loader rows: {realized}/{versions.Length}");
        Check("only visible loader rows are realized", realized > 0 && realized < 30);
        var changes = 0;
        foreach (var version in versions) version.PropertyChanged += Changed;
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoaderVersionModel.IsSelected)) changes++;
        }
        vm.SelectInstallLoaderVersionCommand.Execute(versions[0]);
        changes = 0;
        var started = Stopwatch.GetTimestamp();
        vm.SelectInstallLoaderVersionCommand.Execute(versions[1]);
        Console.WriteLine($"Version selection: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} ms");
        Check("selection updates only the previous and new rows",
            changes == 2 && !versions[0].IsSelected && versions[1].IsSelected &&
            vm.InstallLoaderVersion == versions[1].Version && vm.CanConfirmInstall);
        await Task.Delay(80);
        Shot(window, Path.Combine(output, "loaded.png"));
        list.ScrollIntoView(versions[^1]);
        await Task.Delay(100);
        Check("the last virtualized version remains reachable",
            list.ItemContainerGenerator.ContainerFromIndex(versions.Length - 1) is not null);
        vm.SelectInstallLoaderVersionCommand.Execute(versions[^1]);
        Check("selecting a recycled row uses the correct version",
            vm.InstallLoaderVersion == versions[^1].Version);
        window.Width = 1080;
        window.Height = 740;
        await Task.Delay(100);
        Shot(window, Path.Combine(output, "minimum-size.png"));
        vm.CloseInstallSheetCommand.Execute(null);
        window.ExitApplication();
    }

    private static void Shot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
