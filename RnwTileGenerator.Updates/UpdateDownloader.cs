using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace RnwTileGenerator.Updates;

public sealed record DownloadProgress(long Done, long Total);

public enum DownloadResult
{
    Ok,
    SizeMismatch,
    HashMismatch,
}

/// <summary>
/// Lädt Release-Dateien. Große Dateien werden gestreamt, der SHA-256 wird beim Schreiben mitgerechnet, die Datei entsteht erst als
/// <c>.part</c> und wird nur bei passender Größe und passendem Hash umbenannt. HTTP-Fehler werfen <see cref="HttpRequestException"/>.
/// </summary>
public sealed class UpdateDownloader(HttpClient client)
{
    private const int ReportEveryBytes = 256 * 1024;

    /// <summary>Kleine Datei (Manifest, Signatur) ganz in den Speicher; <see langword="null"/>, wenn sie größer als <paramref name="maxBytes"/> ist.</summary>
    public async Task<byte[]?> GetSmallAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    public async Task<DownloadResult> DownloadFileAsync(
        string url, string targetFile, long expectedSize, string expectedSha256, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (IsComplete(targetFile, expectedSize, expectedSha256))
        {
            progress?.Report(new DownloadProgress(expectedSize, expectedSize));
            return DownloadResult.Ok;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        var partFile = targetFile + ".part";
        var result = DownloadResult.SizeMismatch;
        try
        {
            result = await DownloadToPartAsync(url, partFile, expectedSize, expectedSha256, progress, cancellationToken).ConfigureAwait(false);
            if (result == DownloadResult.Ok)
            {
                File.Move(partFile, targetFile, overwrite: true);
            }

            return result;
        }
        finally
        {
            if (result != DownloadResult.Ok)
            {
                TryDelete(partFile);
                TryDelete(targetFile);
            }
        }
    }

    private async Task<DownloadResult> DownloadToPartAsync(
        string url, string partFile, long expectedSize, string expectedSha256, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(url, cancellationToken).ConfigureAwait(false);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        long lastReport = 0;
        await using (var target = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                done += read;
                if (done > expectedSize)
                {
                    return DownloadResult.SizeMismatch;
                }

                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (done - lastReport >= ReportEveryBytes)
                {
                    lastReport = done;
                    progress?.Report(new DownloadProgress(done, expectedSize));
                }
            }
        }

        progress?.Report(new DownloadProgress(done, expectedSize));
        if (done != expectedSize)
        {
            return DownloadResult.SizeMismatch;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)
            ? DownloadResult.Ok
            : DownloadResult.HashMismatch;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RnwTileGenerator", "update"));
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static bool IsComplete(string file, long size, string sha256)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length != size)
        {
            return false;
        }

        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
