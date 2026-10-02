using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace NegiCraftLauncher.Core.Net;

public static class NclHttp
{
    private static readonly Lazy<HttpClient> LazyClient = new(CreateClient);

    public static HttpClient Shared => LazyClient.Value;

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 32,
        };

        var client = new HttpClient(handler)
        {
            // Per-attempt limits are enforced by the downloader so a slow-but-progressing
            // download of a large jar is never killed by a blanket request timeout.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NegiCraftLauncher", "1.0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }
}
