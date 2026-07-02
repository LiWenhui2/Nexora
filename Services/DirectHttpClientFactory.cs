using System.Net;
using System.Net.Http;

namespace NaiwaProxy.Services;

internal static class DirectHttpClientFactory
{
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All
    };

    public static HttpClient Shared { get; } = new(SharedHandler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
}
