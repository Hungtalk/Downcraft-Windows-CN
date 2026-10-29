using System.Net;
using System.Net.Http.Headers;

namespace DownCraft.Core.Net;

/// <summary>Creates the shared <see cref="HttpClient"/> configuration (connection pooling, compression, UA).</summary>
public static class McdHttp
{
    public static HttpClient Create(int maxParallel, string userAgent)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = Math.Max(4, maxParallel),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }
}
