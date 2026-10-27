using System.Net;
using NegiCraftLauncher.Core.Modpacks;
using NegiCraftLauncher.Core.Net;

var passed = 0;
void Check(string name, bool valid)
{
    if (!valid) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS {name}");
    passed++;
}

const string fabric = """[{"loader":{"version":"0.18.1"}},{"loader":{"version":"0.17.9"}}]""";
const string maven = """
    <metadata><versioning><versions>
    <version>1.20.1-47.1.0</version><version>1.20.1-47.2.0</version>
    <version>1.21.1-52.0.1</version>
    </versions></versioning></metadata>
    """;

using (var handler = new StubHandler((_, _) => Task.FromResult(Text(maven))))
using (var http = new HttpClient(handler))
{
    var results = await Task.WhenAll(
        LoaderCatalog.ListAsync("forge", "1.20.1", OfficialDownloadSource.Instance, http),
        LoaderCatalog.ListAsync("forge", "1.21.1", OfficialDownloadSource.Instance, http));
    Check("Forge builds remain scoped to the selected Minecraft version",
        results[0].SequenceEqual(["47.2.0", "47.1.0"]) && results[1].SequenceEqual(["52.0.1"]));
    Check("different Minecraft versions share one Maven metadata request", handler.Requests == 1);
    await LoaderCatalog.ListAsync("forge", "1.20.1", OfficialDownloadSource.Instance, http);
    Check("revisiting a loader uses the cache", handler.Requests == 1);
}

var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using (var handler = new StubHandler(async (_, ct) =>
{
    started.TrySetResult();
    await release.Task.WaitAsync(ct);
    return Text(fabric);
}))
using (var http = new HttpClient(handler))
using (var cancel = new CancellationTokenSource())
{
    var first = LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http, cancel.Token);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    var second = LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http);
    cancel.Cancel();
    try
    {
        await first;
        Check("cancelled waiter stops immediately", false);
    }
    catch (OperationCanceledException)
    {
        Check("cancelled waiter stops immediately", true);
    }
    release.TrySetResult();
    var result = await second.WaitAsync(TimeSpan.FromSeconds(3));
    Check("cancelling one selection does not cancel another or its cache entry",
        result.SequenceEqual(["0.18.1", "0.17.9"]) && handler.Requests == 1);
    await LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http);
    Check("a cancelled selection can reuse its completed request", handler.Requests == 1);
}

using (var handler = new StubHandler((request, _) =>
    Task.FromResult(Text(request.RequestUri!.Host == "meta.fabricmc.net"
        ? fabric : """<metadata><version>21.1.10</version><version>21.1.11</version></metadata>"""))))
using (var http = new HttpClient(handler))
{
    var neoforge = await LoaderCatalog.ListAsync("neoforge", "1.21.1", OfficialDownloadSource.Instance, http);
    Check("NeoForge keeps the full version required by its installer", neoforge.SequenceEqual(["21.1.11", "21.1.10"]));
    await LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http);
    Check("different loaders have separate cache entries", handler.Requests == 2);
    var unsupported = await LoaderCatalog.ListAsync("neoforge", "24w10a", OfficialDownloadSource.Instance, http);
    Check("unsupported game versions do not make a request", unsupported.Count == 0 && handler.Requests == 2);
}

var attempt = 0;
using (var handler = new StubHandler((_, _) =>
    Task.FromResult(Interlocked.Increment(ref attempt) <= 2
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Text(fabric))))
using (var http = new HttpClient(handler))
{
    try
    {
        await LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http);
        Check("failed responses are surfaced", false);
    }
    catch (IOException)
    {
        Check("failed responses are surfaced", true);
    }
    var retry = await LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http);
    Check("a failed fetch is evicted so retry can succeed", retry.Count == 2 && handler.Requests == 3);
}

using (var handler = new StubHandler((_, _) => Task.FromResult(Text(fabric))))
using (var http = new HttpClient(handler))
{
    await LoaderCatalog.ListAsync("fabric", "1.21.1", OfficialDownloadSource.Instance, http);
    await LoaderCatalog.ListAsync("fabric", "1.21.1", BmclapiDownloadSource.Instance, http);
    Check("switching download sources does not reuse the other source's cache", handler.Requests == 2);
}

using (var handler = new StubHandler(async (request, ct) =>
{
    if (request.RequestUri!.Host == "slow.test") await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    return Text("fallback");
}))
using (var http = new HttpClient(handler))
{
    var text = await HttpText.FetchAsync(http,
        ["https://slow.test/metadata", "https://fallback.test/metadata"],
        attemptTimeout: TimeSpan.FromMilliseconds(30));
    Check("a stalled metadata endpoint times out and falls back", text == "fallback" && handler.Requests == 3);
}

Console.WriteLine($"RESULT: {passed}/{passed} passed");
return 0;

static HttpResponseMessage Text(string content) => new(HttpStatusCode.OK)
{
    Content = new StringContent(content)
};

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    private int _requests;
    public int Requests => Volatile.Read(ref _requests);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _requests);
        return send(request, ct);
    }
}
