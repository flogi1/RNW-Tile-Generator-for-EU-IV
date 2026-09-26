using System.Net;
using System.Security.Cryptography;
using System.Text;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class UpdateDownloaderTests
{
    private sealed class BytesHandler(byte[] body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class SyncProgress(List<DownloadProgress> reports) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => reports.Add(value);
    }

    private static readonly byte[] Body = Enumerable.Range(0, 600_000).Select(i => (byte)(i % 251)).ToArray();
    private static readonly string BodyHash = Convert.ToHexString(SHA256.HashData(Body)).ToLowerInvariant();

    [Fact]
    public async Task Download_schreibt_Datei_und_meldet_Fortschritt()
    {
        using var temp = new TempFolder();
        var target = temp.Combine("dl", "paket.zip");
        var reports = new List<DownloadProgress>();

        var result = await new UpdateDownloader(new HttpClient(new BytesHandler(Body))).DownloadFileAsync(
            "https://github.com/x", target, Body.Length, BodyHash, new SyncProgress(reports), CancellationToken.None);

        Assert.Equal(DownloadResult.Ok, result);
        Assert.Equal(Body, File.ReadAllBytes(target));
        Assert.False(File.Exists(target + ".part"));
        Assert.True(reports.Count >= 2);
        Assert.Equal(new DownloadProgress(Body.Length, Body.Length), reports[^1]);
    }

    [Fact]
    public async Task Falscher_Hash_und_falsche_Groesse_werden_erkannt_und_nichts_bleibt_liegen()
    {
        using var temp = new TempFolder();
        var target = temp.Combine("paket.zip");
        var downloader = new UpdateDownloader(new HttpClient(new BytesHandler(Body)));

        Assert.Equal(DownloadResult.HashMismatch, await downloader.DownloadFileAsync("https://github.com/x", target, Body.Length, new string('0', 64), null, CancellationToken.None));
        Assert.Equal(DownloadResult.SizeMismatch, await downloader.DownloadFileAsync("https://github.com/x", target, Body.Length - 1, BodyHash, null, CancellationToken.None));
        Assert.Equal(DownloadResult.SizeMismatch, await downloader.DownloadFileAsync("https://github.com/x", target, Body.Length + 1, BodyHash, null, CancellationToken.None));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Vorhandene_richtige_Datei_wird_nicht_neu_geladen()
    {
        using var temp = new TempFolder();
        var target = temp.Combine("paket.zip");
        File.WriteAllBytes(target, Body);
        var handler = new BytesHandler(Body);

        var result = await new UpdateDownloader(new HttpClient(handler)).DownloadFileAsync("https://github.com/x", target, Body.Length, BodyHash, null, CancellationToken.None);

        Assert.Equal(DownloadResult.Ok, result);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Vorhandene_falsche_Datei_wird_neu_geladen()
    {
        using var temp = new TempFolder();
        var target = temp.Combine("paket.zip");
        var broken = (byte[])Body.Clone();
        broken[100] ^= 0xFF;
        File.WriteAllBytes(target, broken);
        var handler = new BytesHandler(Body);

        var result = await new UpdateDownloader(new HttpClient(handler)).DownloadFileAsync("https://github.com/x", target, Body.Length, BodyHash, null, CancellationToken.None);

        Assert.Equal(DownloadResult.Ok, result);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(Body, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task Abbruch_wirft_und_raeumt_auf()
    {
        using var temp = new TempFolder();
        var target = temp.Combine("paket.zip");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateDownloader(new HttpClient(new BytesHandler(Body))).DownloadFileAsync("https://github.com/x", target, Body.Length, BodyHash, null, cancellation.Token));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task GetSmall_lehnt_zu_grosse_Antworten_ab()
    {
        var downloader = new UpdateDownloader(new HttpClient(new BytesHandler(Encoding.UTF8.GetBytes("12345"))));
        Assert.Equal("12345", Encoding.UTF8.GetString((await downloader.GetSmallAsync("https://github.com/x", 5, CancellationToken.None))!));
        Assert.Null(await downloader.GetSmallAsync("https://github.com/x", 4, CancellationToken.None));
    }

    [Fact]
    public void Log_haengt_an_und_kuerzt_auf_die_Haelfte()
    {
        using var temp = new TempFolder();
        var log = new UpdateLog(temp.Combine("sub", "update.log"), maxBytes: 2_000);
        for (var i = 0; i < 100; i++)
        {
            log.Write($"Zeile {i:D3}");
        }

        var text = File.ReadAllText(log.Path);
        Assert.True(text.Length < 2_100);
        Assert.Contains("Zeile 099", text);
        Assert.DoesNotContain("Zeile 000", text);
    }

    [Fact]
    public void Pfade_liegen_unter_der_Wurzel()
    {
        var paths = new UpdatePaths(@"C:\x\updates");
        var version = new AppVersion(1, 2, 0);
        Assert.Equal(@"C:\x\updates\1.2.0\staging", paths.StagingFolder(version));
        Assert.Equal(@"C:\x\updates\1.2.0\download", paths.DownloadFolder(version));
        Assert.Equal(@"C:\x\updates\1.2.0\started.marker", paths.Marker(version));
        Assert.Equal(@"C:\x\updates\update.log", paths.LogFile);
    }
}
