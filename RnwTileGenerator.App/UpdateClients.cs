using System;
using System.Net.Http;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// HttpClients for the update check (short timeout) and the download (long timeout, the package is about
/// 70 MB). In Debug builds the environment variable RNW_UPDATE_FEED redirects both to a local feed folder
/// (see LocalFeedHandler).
/// </summary>
internal static class UpdateClients
{
    public static HttpClient Check { get; } = Create(TimeSpan.FromSeconds(10));

    public static HttpClient Download { get; } = Create(TimeSpan.FromMinutes(30));

    private static HttpClient Create(TimeSpan timeout)
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable("RNW_UPDATE_FEED") is { Length: > 0 } feed)
        {
            return new HttpClient(new LocalFeedHandler(feed)) { Timeout = timeout };
        }
#endif
        return new HttpClient { Timeout = timeout };
    }
}
