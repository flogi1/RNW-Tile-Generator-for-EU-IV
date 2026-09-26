using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class ReleaseAssetTests
{
    private const string Json = """
        {"tag_name":"v1.2.0","html_url":"https://github.com/o/r/releases/tag/v1.2.0","assets":[
          {"name":"RnwTileGenerator-1.2.0-win-x64.zip","browser_download_url":"https://github.com/o/r/releases/download/v1.2.0/RnwTileGenerator-1.2.0-win-x64.zip","size":1234},
          {"name":"update-manifest.json","browser_download_url":"https://github.com/o/r/releases/download/v1.2.0/update-manifest.json","size":200},
          {"name":"fremd.zip","browser_download_url":"https://evil.example/fremd.zip","size":5},
          {"name":"ohne-groesse.zip","browser_download_url":"https://github.com/o/r/x.zip"},
          {"name":"falsch.zip","browser_download_url":"https://github.com.evil.example/x.zip","size":5}
        ]}
        """;

    [Fact]
    public void Parse_liest_nur_gueltige_GitHub_Assets()
    {
        var release = ReleaseParser.Parse(Json)!;

        Assert.Equal(new[] { "RnwTileGenerator-1.2.0-win-x64.zip", "update-manifest.json" }, release.Assets.Select(a => a.Name));
        var zip = release.FindAsset("RnwTileGenerator-1.2.0-win-x64.zip")!;
        Assert.Equal(1234, zip.Size);
        Assert.StartsWith("https://github.com/o/r/releases/download/", zip.DownloadUrl);
        Assert.Null(release.FindAsset("fremd.zip"));
        Assert.Null(release.FindAsset("Update-Manifest.json"));
    }

    [Fact]
    public void Parse_ohne_Assets_liefert_leere_Liste()
    {
        var release = ReleaseParser.Parse("""{"tag_name":"v1.0.0","html_url":"https://github.com/o/r"}""")!;
        Assert.Empty(release.Assets);
    }

    [Theory]
    [InlineData("https://github.com/o/r/x.zip", true)]
    [InlineData("HTTPS://GITHUB.COM/o/r/x.zip", true)]
    [InlineData("http://github.com/o/r/x.zip", false)]
    [InlineData("https://github.com.evil.example/x.zip", false)]
    [InlineData("https://objects.githubusercontent.com/x.zip", false)]
    public void IsGitHubUrl_prueft_Schema_und_Host(string url, bool expected)
    {
        Assert.Equal(expected, ReleaseParser.IsGitHubUrl(url));
    }
}
