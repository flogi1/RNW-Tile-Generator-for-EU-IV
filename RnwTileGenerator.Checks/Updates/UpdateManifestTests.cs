using System.Text;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class UpdateManifestTests
{
    private const string Zip = "RnwTileGenerator-1.2.0-win-x64.zip";
    private static readonly string Hash = new('a', 64);

    private static ReleaseInfo Release(string tag = "v1.2.0", long zipSize = 100) =>
        new(AppVersion.TryParse(tag, out var v) ? v : default, tag, "https://github.com/o/r", [new ReleaseAsset(Zip, "https://github.com/o/r/" + Zip, zipSize)]);

    private static UpdateManifest Manifest(string version = "1.2.0", int protocol = 1) =>
        UpdateManifest.Parse(Encoding.UTF8.GetBytes($$"""{"version":"{{version}}","asset":"{{Zip}}","sha256":"{{Hash}}","size":100,"minApplyProtocol":{{protocol}}}"""))!;

    [Fact]
    public void Parse_und_ToJson_ergeben_denselben_Inhalt()
    {
        var manifest = Manifest();
        Assert.Equal(new AppVersion(1, 2, 0), manifest.Version);
        Assert.Equal(manifest, UpdateManifest.Parse(manifest.ToJson()));
    }

    [Theory]
    [InlineData("""{"version":"1.2.0","asset":"a.zip","sha256":"xyz","size":1,"minApplyProtocol":1}""")]
    [InlineData("""{"version":"1.2.0","asset":"../a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1,"minApplyProtocol":1}""")]
    [InlineData("""{"version":"1.2.0","asset":"a.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1,"minApplyProtocol":1}""")]
    [InlineData("""{"version":"neu","asset":"a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1,"minApplyProtocol":1}""")]
    [InlineData("""{"version":"1.2.0","asset":"a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":0,"minApplyProtocol":1}""")]
    [InlineData("""{"version":"1.2.0","asset":"a.zip","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","size":1}""")]
    [InlineData("kein json")]
    public void Parse_lehnt_unbrauchbare_Manifeste_ab(string json)
    {
        Assert.Null(UpdateManifest.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Check_akzeptiert_passendes_Manifest()
    {
        Assert.Equal(UpdateProblem.None, Manifest().Check(Release(), new AppVersion(1, 1, 0)));
    }

    [Fact]
    public void Check_meldet_jede_verletzte_Regel()
    {
        Assert.Equal(UpdateProblem.VersionMismatch, Manifest("1.1.5").Check(Release(), new AppVersion(1, 0, 0)));
        Assert.Equal(UpdateProblem.NotNewer, Manifest().Check(Release(), new AppVersion(1, 2, 0)));
        Assert.Equal(UpdateProblem.NotNewer, Manifest().Check(Release(), new AppVersion(2, 0, 0)));
        Assert.Equal(UpdateProblem.ProtocolTooNew, Manifest(protocol: 2).Check(Release(), new AppVersion(1, 0, 0)));
        Assert.Equal(UpdateProblem.SizeMismatch, Manifest().Check(Release(zipSize: 99), new AppVersion(1, 0, 0)));
        var noZip = Release() with { Assets = [] };
        Assert.Equal(UpdateProblem.AssetMissing, Manifest().Check(noZip, new AppVersion(1, 0, 0)));
    }

    [Fact]
    public void Create_berechnet_Hash_und_Groesse_der_ZIP()
    {
        using var temp = new TempFolder();
        var zip = temp.WriteFile(Zip, "hallo");
        var manifest = UpdateManifest.Create(zip, new AppVersion(1, 2, 0));

        Assert.Equal(Zip, manifest.Asset);
        Assert.Equal(5, manifest.Size);
        Assert.Equal("d3751d33f9cd5049c4af2b462735457e4d3baf130bcbb87f389e349fbaeb20b9", manifest.Sha256);
        Assert.Equal(UpdateManifest.SupportedApplyProtocol, manifest.MinApplyProtocol);
    }
}
