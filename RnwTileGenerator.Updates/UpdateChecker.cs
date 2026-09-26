using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RnwTileGenerator.Updates;

/// <summary>Eine Datei eines GitHub-Releases (nur Dateien mit GitHub-Download-Adresse).</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>Neueste veröffentlichte Version laut GitHub-Release.</summary>
public sealed record ReleaseInfo(AppVersion Version, string TagName, string HtmlUrl, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>Asset mit genau diesem Namen (Groß-/Kleinschreibung zählt), sonst <see langword="null"/>.</summary>
    public ReleaseAsset? FindAsset(string name) => Assets.FirstOrDefault(asset => string.Equals(asset.Name, name, StringComparison.Ordinal));
}

public enum UpdateCheckStatus
{
    /// <summary>Die installierte Version ist die neueste (oder es gibt noch gar kein Release).</summary>
    UpToDate,

    UpdateAvailable,

    /// <summary>Keine Verbindung, GitHub-Fehler oder unlesbare Antwort - Details in <see cref="UpdateCheckResult.Error"/>.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release = null, string? Error = null);

/// <summary>
/// Fragt das neueste Release eines öffentlichen GitHub-Repositorys ab (<c>GET /repos/{repo}/releases/latest</c>,
/// ohne Anmeldung) und vergleicht es mit der installierten Version. Sendet keine Nutzerdaten außer dem
/// üblichen HTTP-Aufruf. Wirft nie: Fehler werden als <see cref="UpdateCheckStatus.Failed"/> zurückgegeben.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _client;
    private readonly string _repository;

    public UpdateChecker(HttpClient client, string repository)
    {
        _client = client;
        _repository = repository;
    }

    public async Task<UpdateCheckResult> CheckAsync(AppVersion current, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RnwTileGenerator", current.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // Noch kein Release veröffentlicht: kein Fehler, es gibt schlicht nichts Neueres.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: $"GitHub antwortete mit Status {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = ReleaseParser.Parse(json);
            if (release is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: "Die Antwort von GitHub konnte nicht gelesen werden.");
            }

            return release.Version > current
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Error: ex.Message);
        }
    }
}

/// <summary>Liest die für den Update-Hinweis nötigen Felder aus der GitHub-Release-Antwort.</summary>
public static class ReleaseParser
{
    /// <summary>
    /// <see langword="null"/> bei ungültigem JSON, fehlenden Feldern, nicht als Version lesbarem Tag sowie bei
    /// Entwürfen und Vorabversionen (die der Nutzer nicht angeboten bekommen soll).
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return null;
            }

            if (!root.TryGetProperty("tag_name", out var tagElement)
                || !root.TryGetProperty("html_url", out var urlElement)
                || tagElement.GetString() is not { } tag
                || urlElement.GetString() is not { } url
                || !AppVersion.TryParse(tag, out var version))
            {
                return null;
            }

            return new ReleaseInfo(version, tag, url, ReadAssets(root));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Nur Adressen direkt auf github.com (https) werden angenommen; Weiterleitungen folgt später der HttpClient.</summary>
    public static bool IsGitHubUrl(string url) => url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    private static List<ReleaseAsset> ReadAssets(JsonElement root)
    {
        var assets = new List<ReleaseAsset>();
        if (!root.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return assets;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && item.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String
                && item.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var bytes)
                && IsGitHubUrl(url.GetString()!))
            {
                assets.Add(new ReleaseAsset(name.GetString()!, url.GetString()!, bytes));
            }
        }

        return assets;
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>Entscheidet, ob der automatische Update-Check beim Start laufen soll.</summary>
public static class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static bool IsDue(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is null || nowUtc - lastCheckUtc.Value >= Interval || lastCheckUtc.Value > nowUtc;
}
