using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Modrinth;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;

internal static class Program
{
    private static int _failed;
    private static int _checked;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new NegiCraftLauncher.App.App();
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var frame = new DispatcherFrame();
        var run = Run(args.FirstOrDefault(), () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        run.GetAwaiter().GetResult();
        app.Shutdown();
        Console.WriteLine($"RESULT: {_checked - _failed}/{_checked} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        _checked++;
        if (!ok) _failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("Test condition timed out.");
            await Task.Delay(10);
        }
    }

    private static async Task Run(string? output, Action done)
    {
        try
        {
            await CheckBrowsing();
            await CheckCancellation();
            await CheckCacheBounds();
            await CheckVirtualization(output);
        }
        catch (Exception ex)
        {
            Check("unhandled regression exception", false);
            Console.WriteLine(ex);
        }
        finally { done(); }
    }

    private static async Task CheckBrowsing()
    {
        using var handler = new SearchHandler();
        using var http = new HttpClient(handler);
        var browser = new ResourceBrowserViewModel(new ModrinthClient(http));
        var games = Games();
        var publications = 0;
        browser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(browser.FilteredResources)) publications++;
        };
        browser.SetGameVersions(games);
        Check("200 game rows published with one list notification", publications == 1);
        for (var i = 0; i < 20; i++)
        {
            browser.CurrentResCategory = "pack";
            Check($"unsupported pack category responds immediately ({i})", !browser.IsLoadingResources && !browser.HasResources);
            browser.CurrentResCategory = "game";
        }
        Check("revisiting games reuses the original list", ReferenceEquals(games, browser.FilteredResources));
        Check("games and packs do not send search requests", handler.Urls.Count == 0);

        browser.ResourceSearch = "1.20";
        var filtered = browser.FilteredResources;
        browser.CurrentResCategory = "pack";
        browser.CurrentResCategory = "game";
        Check("filtered game list survives category switches", ReferenceEquals(filtered, browser.FilteredResources)
            && filtered.Count == 1 && filtered[0].Name == "1.20");
        browser.ResourceSearch = "";
        browser.CurrentResCategory = "mod";
        Check("missing instance is handled without debounce or HTTP", !browser.IsLoadingResources && handler.Urls.Count == 0);

        browser.SetInstance(Installation("first", "1.21", "Fabric"));
        Check("category request starts immediately", handler.Urls.Count == 1);
        await Until(() => !browser.IsLoadingResources);
        var mods = browser.FilteredResources;
        Check("search publishes resources and empty-state notification", browser.HasResources && mods.Count == 30);
        Check("mod request includes version and loader facets", handler.Urls[0].Contains("versions:1.21")
            && handler.Urls[0].Contains("categories:fabric"));
        Check("resource rows share one icon", mods.All(r => ReferenceEquals(r.IconBitmap, mods[0].IconBitmap)));
        mods[0].ActionText = "installed-test";
        mods[0].IsActionEnabled = false;

        browser.CurrentResCategory = "rp";
        await Until(() => !browser.IsLoadingResources);
        var packs = browser.FilteredResources;
        Check("resource packs are not filtered by mod loader", !handler.Urls[^1].Contains("categories:fabric"));
        browser.CurrentResCategory = "shader";
        await Until(() => !browser.IsLoadingResources);
        Check("shaders are not filtered by mod loader", !handler.Urls[^1].Contains("categories:fabric"));
        var requests = handler.Urls.Count;
        for (var i = 0; i < 20; i++)
        {
            foreach (var category in new[] { "game", "mod", "pack", "rp", "shader" })
                browser.CurrentResCategory = category;
        }
        Check("100 warm category switches make no new HTTP requests", handler.Urls.Count == requests);
        browser.CurrentResCategory = "mod";
        Check("cached mods keep row identity and install state", ReferenceEquals(mods, browser.FilteredResources)
            && !browser.FilteredResources[0].IsActionEnabled);
        browser.CurrentResCategory = "rp";
        Check("cached resource packs keep list identity", ReferenceEquals(packs, browser.FilteredResources));

        browser.CurrentResCategory = "mod";
        browser.ResourceSearch = "s";
        browser.ResourceSearch = "so";
        browser.ResourceSearch = "sodium";
        await Task.Delay(100);
        Check("typing does not immediately flood the API", handler.Urls.Count == requests);
        await Until(() => !browser.IsLoadingResources);
        Check("typing produces only one settled search", handler.Urls.Count == requests + 1
            && handler.Urls[^1].Contains("query=sodium"));
        var sodium = browser.FilteredResources;
        Check("icon is reused across searches", ReferenceEquals(sodium[0].IconBitmap, mods[0].IconBitmap));
        browser.ResourceSearch = " sodium ";
        Check("trimmed cached query has no delay", !browser.IsLoadingResources && ReferenceEquals(sodium, browser.FilteredResources));

        browser.SetInstance(Installation("second", "1.21", "Fabric"));
        await Until(() => !browser.IsLoadingResources);
        Check("same-version instances do not share installation action state", !ReferenceEquals(sodium, browser.FilteredResources)
            && browser.FilteredResources[0].IsActionEnabled);
        browser.SetInstance(Installation("second", "1.20", "Forge"));
        await Until(() => !browser.IsLoadingResources);
        Check("changed version and loader refresh matching results", handler.Urls[^1].Contains("versions:1.20")
            && handler.Urls[^1].Contains("categories:forge"));
        browser.SetGameVersionError("manifest-test");
        Check("background manifest failure does not overwrite active search", !browser.ResourceEmptyText.Contains("manifest-test"));
        browser.CurrentResCategory = "game";
        Check("manifest error survives returning to games", browser.ResourceEmptyText.Contains("manifest-test"));
    }

    private static async Task CheckCancellation()
    {
        var old = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new SearchHandler((url, _) => url.Contains("project_type:mod") ? old.Task : next.Task);
        using var http = new HttpClient(handler);
        var browser = new ResourceBrowserViewModel(new ModrinthClient(http));
        browser.SetInstance(Installation("first", "1.21", "Fabric"));
        browser.CurrentResCategory = "mod";
        browser.CurrentResCategory = "shader";
        Check("switching categories cancels the previous request", handler.Tokens[0].IsCancellationRequested);
        old.SetResult(Response("old"));
        await Task.Delay(50);
        Check("old completion cannot hide the new loading state", browser.IsLoadingResources && !browser.HasResources);
        next.SetResult(Response("new"));
        await Until(() => !browser.IsLoadingResources);
        Check("only current response publishes rows", browser.FilteredResources.All(r => r.Category == "shader" && r.Name.StartsWith("new")));

        var failure = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        using var failingHandler = new SearchHandler((_, _) =>
        {
            if (++attempt == 1) throw new IOException("first attempt");
            return failure.Task;
        });
        using var failingHttp = new HttpClient(failingHandler);
        var failing = new ResourceBrowserViewModel(new ModrinthClient(failingHttp));
        failing.SetInstance(Installation("first", "1.21", "Fabric"));
        failing.CurrentResCategory = "mod";
        await Until(() => attempt == 2);
        failing.CurrentResCategory = "pack";
        var text = failing.ResourceEmptyText;
        failure.SetException(new IOException("late failure"));
        await Task.Delay(50);
        Check("late failed request cannot overwrite another category", failing.ResourceEmptyText == text && !failing.IsLoadingResources);
    }

    private static async Task CheckCacheBounds()
    {
        using var handler = new SearchHandler();
        using var http = new HttpClient(handler);
        var clock = new TestClock();
        var browser = new ResourceBrowserViewModel(new ModrinthClient(http), clock, cacheCapacity: 2);
        browser.SetInstance(Installation("first", "1.21", "Fabric"));
        foreach (var category in new[] { "mod", "rp", "mod", "shader" })
        {
            browser.CurrentResCategory = category;
            await Until(() => !browser.IsLoadingResources);
        }
        Check("recently used cache entry is retained", handler.Urls.Count == 3);
        browser.CurrentResCategory = "rp";
        await Until(() => !browser.IsLoadingResources);
        Check("bounded cache evicts the least recently used page", handler.Urls.Count == 4);
        clock.Advance(TimeSpan.FromMinutes(6));
        browser.CurrentResCategory = "pack";
        browser.CurrentResCategory = "rp";
        await Until(() => !browser.IsLoadingResources);
        Check("expired cached results are refreshed", handler.Urls.Count == 5);

        using var emptyHandler = new SearchHandler((_, _) => Task.FromResult(Response("empty", 0)));
        using var emptyHttp = new HttpClient(emptyHandler);
        var empty = new ResourceBrowserViewModel(new ModrinthClient(emptyHttp));
        empty.SetInstance(Installation("first", "1.21", "Fabric"));
        empty.CurrentResCategory = "mod";
        await Until(() => !empty.IsLoadingResources);
        empty.CurrentResCategory = "pack";
        empty.CurrentResCategory = "mod";
        Check("successful empty results are cached", emptyHandler.Urls.Count == 1 && !empty.HasResources && !empty.IsLoadingResources);
    }

    private static async Task CheckVirtualization(string? output)
    {
        var factory = new NegiCraftLauncher.App.MainWindow();
        Window? host = null;
        try
        {
            var list = (ListBox)factory.FindName("ResourceList");
            var border = (Border)list.Parent;
            ((Panel)border.Parent).Children.Remove(border);
            var browser = new HostViewModel();
            browser.SetGameVersions(Games());
            host = new Window
            {
                Content = border, DataContext = browser,
                Width = 1000, Height = 640, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            host.Show();
            await Task.Delay(80);
            host.UpdateLayout();
            var realized = list.Items.Cast<object>().Count(item => list.ItemContainerGenerator.ContainerFromItem(item) is not null);
            Console.WriteLine($"WPF realized rows: {realized}/{list.Items.Count}");
            Check("actual resource list virtualizes 200 game rows", realized > 0 && realized < 50 && list.Items.Count == 200);
            Check("resource list uses container recycling", VirtualizingPanel.GetVirtualizationMode(list) == VirtualizationMode.Recycling);
            list.ScrollIntoView(browser.FilteredResources[^1]);
            await Task.Delay(50);
            host.UpdateLayout();
            Check("last row is reachable by scrolling", list.ItemContainerGenerator.ContainerFromItem(browser.FilteredResources[^1]) is ListBoxItem);
            list.ScrollIntoView(browser.FilteredResources[0]);
            await Task.Delay(50);
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(browser.FilteredResources[0]);
            var button = Descendants<Button>(container).Single();
            button.Command.Execute(button.CommandParameter);
            Check("virtualized row action retains the correct resource", ReferenceEquals(browser.LastInstalled, browser.FilteredResources[0]));

            var timings = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                var timer = Stopwatch.StartNew();
                browser.CurrentResCategory = "pack";
                browser.CurrentResCategory = "game";
                host.UpdateLayout();
                timings.Add(timer.Elapsed.TotalMilliseconds);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            Console.WriteLine($"WPF 30 pack/game switch+layout pairs: median={timings.Order().ElementAt(15):F3}ms, max={timings.Max():F3}ms");
            Check("repeated switches keep virtualization active", list.Items.Cast<object>().Count(item =>
                list.ItemContainerGenerator.ContainerFromItem(item) is not null) < 50);
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                var bitmap = new RenderTargetBitmap((int)border.ActualWidth, (int)border.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(border);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, "resources-wpf.png"));
                encoder.Save(file);
            }

            // Same row template and viewport, but the old non-virtualized collection/list shape.
            var legacyRows = new ObservableCollection<ResourceModel>();
            var legacyList = new ItemsControl { ItemTemplate = list.ItemTemplate, ItemsSource = legacyRows };
            border.Child = legacyList;
            host.Content = null;
            border.VerticalAlignment = VerticalAlignment.Top;
            host.Content = new ScrollViewer
            {
                Content = border, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var legacyTimings = new List<double>();
            var games = Games();
            for (var i = 0; i < 5; i++)
            {
                var timer = Stopwatch.StartNew();
                legacyRows.Clear();
                browser.CurrentResCategory = "pack";
                foreach (var row in games) legacyRows.Add(row);
                browser.CurrentResCategory = "game";
                host.UpdateLayout();
                legacyTimings.Add(timer.Elapsed.TotalMilliseconds);
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            Check("same-template legacy comparison realizes all 200 rows", legacyList.Items.Cast<object>().Count(item =>
                legacyList.ItemContainerGenerator.ContainerFromItem(item) is not null) == 200);
            Console.WriteLine($"Legacy same-template 5 pack/game switch+layout pairs: median={legacyTimings.Order().ElementAt(2):F3}ms, max={legacyTimings.Max():F3}ms");
        }
        finally
        {
            host?.Close();
            factory.ExitApplication();
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static ResourceModel[] Games() => Enumerable.Range(0, 200).Select(i => new ResourceModel
    {
        Name = $"1.{i}", Desc = "Minecraft release", Category = "game", VersionId = $"1.{i}",
        IconBitmap = GameIcon,
    }).ToArray();

    private static PixelBuffer GameIcon { get; } = PixelArt.CreateBlock("grass");
    private static Instance Installation(string id, string version, string loader) =>
        new() { Id = id, VersionId = version, Loader = loader };

    private static HttpResponseMessage Response(string prefix, int count = 30) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            hits = Enumerable.Range(0, count).Select(i => new
            {
                project_id = $"{prefix}-{i}", title = $"{prefix}-{i}",
                description = "Test resource", downloads = 10000, project_type = "mod",
            }),
        }), Encoding.UTF8, "application/json"),
    };

    private sealed class SearchHandler(Func<string, CancellationToken, Task<HttpResponseMessage>>? respond = null) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        public List<CancellationToken> Tokens { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            Urls.Add(url);
            Tokens.Add(token);
            return respond?.Invoke(url, token) ?? Task.FromResult(Response($"request-{Urls.Count}"));
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class HostViewModel : ResourceBrowserViewModel
    {
        public ResourceModel? LastInstalled { get; private set; }
        public IRelayCommand<ResourceModel> InstallResourceCommand { get; }
        public HostViewModel() => InstallResourceCommand = new RelayCommand<ResourceModel>(row => LastInstalled = row);
    }
}
