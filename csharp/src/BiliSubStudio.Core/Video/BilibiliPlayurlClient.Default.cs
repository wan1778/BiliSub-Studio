using System.Net;

namespace BiliSubStudio.Core.Video;

public sealed partial class BilibiliPlayurlClient
{
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(45),
    };

    public BilibiliPlayurlClient() : this(SharedHttp) { }
}
