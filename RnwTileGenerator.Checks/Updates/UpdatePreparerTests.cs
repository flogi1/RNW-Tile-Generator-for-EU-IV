using System.IO.Compression;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

/// <summary>Ende-zu-Ende über den lokalen Feed: echtes ZIP, echtes Manifest, echte Signatur mit Testschlüssel.</summary>
public class UpdatePreparerTests : IDisposable
{
    private static readonly AppVersion NewVersion = new(1, 2, 0);
    private static readonly AppVersion Installed = new(1, 1, 0);
    private static readonly (string PublicKey, byte[] PrivateKey) Key = ManifestSignature.CreateKeyPair("test");

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Feed => _temp.Combine("feed");

    private UpdatePaths Paths => new(_temp.Combine("updates"));

    /// <summary>Baut ein vollständiges Release in den Feed-Ordner.</summary>
    private void BuildRelease(AppVersion version, AppVersion? tag = null, bool sign = true)
    {
        _temp.WriteFile(Path.Combine("app", UpdatePaths.ExecutableName), "exe " + version);
        _temp.WriteFile(Path.Combine("app", "locales", "de.json"), "{}");
        Directory.CreateDirectory(Feed);
        var zip = Path.Combine(Feed, ReleasePackaging.ZipName(version));
        ReleasePackaging.CreateZip(_temp.Combine("app"), zip);
        var manifest = UpdateManifest.Create(zip, version).ToJson();
        File.WriteAllBytes(Path.Combine(Feed, UpdatePreparer.ManifestName), manifest);
        if (sign)
        {
            File.WriteAllText(Path.Combine(Feed, UpdatePreparer.SignatureName), ManifestSignature.Sign(manifest, Key.PrivateKey, "test"));
        }

        ReleasePackaging.WriteLocalFeed(Feed, tag ?? version);
    }

    private async Task<(ReleaseInfo Release, PrepareResult Result)> PrepareAsync(string? publicKey = null)
    {
        var client = new HttpClient(new LocalFeedHandler(Feed));
        var check = await new UpdateChecker(client, "o/r").CheckAsync(Installed);
        var release = check.Release!;
        var preparer = new UpdatePreparer(new UpdateDownloader(client), Paths, publicKey ?? Key.PublicKey, new UpdateLog(_temp.Combine("log.txt")));
        return (release, await preparer.PrepareAsync(release, Installed, null, CancellationToken.None));
    }

    [Fact]
    public async Task Gueltiges_Release_wird_geprueft_und_entpackt()
    {
        BuildRelease(NewVersion);

        var (_, result) = await PrepareAsync();

        Assert.Equal(UpdateProblem.None, result.Problem);
        Assert.Equal(NewVersion, result.Update!.Version);
        Assert.Equal(Paths.StagingFolder(NewVersion), result.Update.StagingFolder);
        Assert.Equal("exe 1.2.0", File.ReadAllText(Path.Combine(result.Update.StagingFolder, UpdatePaths.ExecutableName)));
        Assert.True(File.Exists(Path.Combine(result.Update.StagingFolder, "locales", "de.json")));
        var list = File.ReadAllLines(Path.Combine(result.Update.StagingFolder, UpdatePaths.InstallListName));
        Assert.Equal(new[] { "locales", UpdatePaths.ExecutableName }, list);
    }

    [Fact]
    public async Task Ohne_Signatur_oder_ohne_Schluessel_gibt_es_kein_Paket()
    {
        BuildRelease(NewVersion, sign: false);
        Assert.Equal(UpdateProblem.NoSignedPackage, (await PrepareAsync()).Result.Problem);

        BuildRelease(NewVersion);
        Assert.Equal(UpdateProblem.NoSignedPackage, (await PrepareAsync(publicKey: "")).Result.Problem);
    }

    [Fact]
    public async Task Veraendertes_Manifest_hat_ungueltige_Signatur()
    {
        BuildRelease(NewVersion);
        var manifestFile = Path.Combine(Feed, UpdatePreparer.ManifestName);
        File.WriteAllText(manifestFile, File.ReadAllText(manifestFile).Replace("1.2.0", "1.2.1"));
        ReleasePackaging.WriteLocalFeed(Feed, NewVersion);

        Assert.Equal(UpdateProblem.BadSignature, (await PrepareAsync()).Result.Problem);
    }

    [Fact]
    public async Task Altes_signiertes_Paket_unter_neuem_Tag_wird_abgelehnt()
    {
        BuildRelease(NewVersion, tag: new AppVersion(9, 9, 9));
        Assert.Equal(UpdateProblem.VersionMismatch, (await PrepareAsync()).Result.Problem);
    }

    [Fact]
    public async Task Ausgetauschte_ZIP_gleicher_Groesse_faellt_beim_Hash_auf()
    {
        BuildRelease(NewVersion);
        var zip = Path.Combine(Feed, ReleasePackaging.ZipName(NewVersion));
        var bytes = File.ReadAllBytes(zip);
        bytes[^30] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        var (_, result) = await PrepareAsync();

        Assert.Equal(UpdateProblem.HashMismatch, result.Problem);
        Assert.False(Directory.Exists(Paths.StagingFolder(NewVersion)));
    }

    [Fact]
    public async Task Alter_Staging_Rest_wird_ersetzt()
    {
        BuildRelease(NewVersion);
        var leftover = Path.Combine(Paths.StagingFolder(NewVersion), "alt.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(leftover)!);
        File.WriteAllText(leftover, "rest");

        var (_, result) = await PrepareAsync();

        Assert.Equal(UpdateProblem.None, result.Problem);
        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task ZIP_mit_Pfad_nach_draussen_wird_nicht_entpackt()
    {
        Directory.CreateDirectory(Feed);
        var zip = Path.Combine(Feed, ReleasePackaging.ZipName(NewVersion));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(UpdatePaths.ExecutableName).Open()))
            {
                writer.Write("exe");
            }

            using (var writer = new StreamWriter(archive.CreateEntry("../../boese.txt").Open()))
            {
                writer.Write("boese");
            }
        }

        var manifest = UpdateManifest.Create(zip, NewVersion).ToJson();
        File.WriteAllBytes(Path.Combine(Feed, UpdatePreparer.ManifestName), manifest);
        File.WriteAllText(Path.Combine(Feed, UpdatePreparer.SignatureName), ManifestSignature.Sign(manifest, Key.PrivateKey, "test"));
        ReleasePackaging.WriteLocalFeed(Feed, NewVersion);

        var (_, result) = await PrepareAsync();

        Assert.Equal(UpdateProblem.ExtractFailed, result.Problem);
        Assert.False(File.Exists(Path.Combine(Paths.Root, "boese.txt")));
        Assert.False(Directory.Exists(Paths.StagingFolder(NewVersion)));
    }

    [Fact]
    public async Task Fehlende_Datei_im_Feed_ist_ein_Netzwerkfehler()
    {
        BuildRelease(NewVersion);
        var client = new HttpClient(new LocalFeedHandler(Feed));
        var release = (await new UpdateChecker(client, "o/r").CheckAsync(Installed)).Release!;
        File.Delete(Path.Combine(Feed, ReleasePackaging.ZipName(NewVersion)));
        var preparer = new UpdatePreparer(new UpdateDownloader(client), Paths, Key.PublicKey, new UpdateLog(_temp.Combine("log.txt")));

        var result = await preparer.PrepareAsync(release, Installed, null, CancellationToken.None);

        Assert.Equal(UpdateProblem.Network, result.Problem);
    }
}
