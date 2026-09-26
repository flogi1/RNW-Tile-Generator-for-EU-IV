using System.IO.Compression;
using System.Text;

namespace RnwTileGenerator.Updates;

public sealed record PreparedUpdate(AppVersion Version, string StagingFolder);

public sealed record PrepareResult(PreparedUpdate? Update, UpdateProblem Problem, string? Detail = null);

/// <summary>
/// Bereitet ein Update vor: Manifest und Signatur laden, Signatur und Regeln prüfen, ZIP laden und prüfen, erst dann nach
/// <c>staging</c> entpacken. Aus dem Download wird vorher nichts ausgeführt. Wirft nur bei Abbruch.
/// </summary>
public sealed class UpdatePreparer(UpdateDownloader downloader, UpdatePaths paths, string publicKey, UpdateLog log)
{
    public const string ManifestName = "update-manifest.json";

    public const string SignatureName = "update-manifest.json.sig";

    private const int MaxSmallFileBytes = 64 * 1024;

    public async Task<PrepareResult> PrepareAsync(ReleaseInfo release, AppVersion installed, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        log.Write($"Vorbereitung {release.TagName} (installiert {installed})");
        try
        {
            var result = await PrepareCoreAsync(release, installed, progress, cancellationToken).ConfigureAwait(false);
            log.Write(result.Problem == UpdateProblem.None ? "Vorbereitung fertig." : $"Vorbereitung abgelehnt: {result.Problem} {result.Detail}");
            return result;
        }
        catch (HttpRequestException ex)
        {
            log.Write("Download fehlgeschlagen: " + ex.Message);
            return new PrepareResult(null, UpdateProblem.Network, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Dateifehler bei der Vorbereitung: " + ex.Message);
            return new PrepareResult(null, UpdateProblem.ExtractFailed, ex.Message);
        }
    }

    private async Task<PrepareResult> PrepareCoreAsync(ReleaseInfo release, AppVersion installed, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (publicKey.Length == 0 || release.FindAsset(ManifestName) is not { } manifestAsset || release.FindAsset(SignatureName) is not { } signatureAsset)
        {
            return Fail(UpdateProblem.NoSignedPackage);
        }

        var manifestBytes = await downloader.GetSmallAsync(manifestAsset.DownloadUrl, MaxSmallFileBytes, cancellationToken).ConfigureAwait(false);
        var signatureBytes = await downloader.GetSmallAsync(signatureAsset.DownloadUrl, MaxSmallFileBytes, cancellationToken).ConfigureAwait(false);
        if (manifestBytes is null || signatureBytes is null)
        {
            return Fail(UpdateProblem.InvalidManifest);
        }

        if (!ManifestSignature.Verify(manifestBytes, Encoding.ASCII.GetString(signatureBytes), publicKey))
        {
            return Fail(UpdateProblem.BadSignature);
        }

        if (UpdateManifest.Parse(manifestBytes) is not { } manifest)
        {
            return Fail(UpdateProblem.InvalidManifest);
        }

        var problem = manifest.Check(release, installed);
        if (problem != UpdateProblem.None)
        {
            return Fail(problem);
        }

        var zipAsset = release.FindAsset(manifest.Asset)!;
        var zipFile = Path.Combine(paths.DownloadFolder(manifest.Version), manifest.Asset);
        var download = await downloader.DownloadFileAsync(zipAsset.DownloadUrl, zipFile, manifest.Size, manifest.Sha256, progress, cancellationToken).ConfigureAwait(false);
        if (download != DownloadResult.Ok)
        {
            return Fail(download == DownloadResult.HashMismatch ? UpdateProblem.HashMismatch : UpdateProblem.SizeMismatch);
        }

        var staging = paths.StagingFolder(manifest.Version);
        if (!await Task.Run(() => TryExtract(zipFile, staging), cancellationToken).ConfigureAwait(false))
        {
            return Fail(UpdateProblem.ExtractFailed);
        }

        return new PrepareResult(new PreparedUpdate(manifest.Version, staging), UpdateProblem.None);
    }

    /// <summary>
    /// Entpackt frisch (alte Reste werden vorher gelöscht). <see cref="ZipFile.ExtractToDirectory(string, string)"/> weist Einträge, die aus
    /// dem Zielordner hinausführen, selbst ab. Ohne Programmdatei und Dateiliste in der Wurzel gilt das Paket als unbrauchbar.
    /// </summary>
    private bool TryExtract(string zipFile, string staging)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipFile, staging);
            if (File.Exists(Path.Combine(staging, UpdatePaths.ExecutableName)) && File.Exists(Path.Combine(staging, UpdatePaths.InstallListName)))
            {
                return true;
            }

            log.Write("Paket enthält keine Programmdatei oder keine Dateiliste.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            log.Write("Entpacken fehlgeschlagen: " + ex.Message);
        }

        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static PrepareResult Fail(UpdateProblem problem) => new(null, problem);
}
