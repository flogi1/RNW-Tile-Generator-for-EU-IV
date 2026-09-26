using System.Net;

namespace RnwTileGenerator.Updates;

/// <summary>
/// Beantwortet die GitHub-Aufrufe des Updates aus einem lokalen Ordner: <c>…/releases/latest</c> liefert <c>release.json</c>,
/// Adressen unter <see cref="BaseUrl"/> liefern die gleichnamige Datei. Nur für Tests und Debug-Builds (<c>RNW_UPDATE_FEED</c>). Weil
/// die Adressen echte github.com-Adressen bleiben, laufen alle Prüfungen unverändert.
/// </summary>
public sealed class LocalFeedHandler(string folder) : HttpMessageHandler
{
    public const string BaseUrl = "https://github.com/local-feed/";

    public const string ReleaseFile = "release.json";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        string? file = null;
        if (uri.Host == "api.github.com" && uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
        {
            file = Path.Combine(folder, ReleaseFile);
        }
        else if (uri.AbsoluteUri.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            var name = Uri.UnescapeDataString(uri.AbsoluteUri[BaseUrl.Length..]);
            if (name == Path.GetFileName(name))
            {
                file = Path.Combine(folder, name);
            }
        }

        if (file is null || !File.Exists(file))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(File.OpenRead(file)),
            RequestMessage = request,
        });
    }
}
