using RnwTileGenerator.Release;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class ReleaseVersionTests
{
    private const string Csproj = "<Project>\r\n  <PropertyGroup>\r\n    <Version>0.3.0</Version>\r\n    <Nullable>enable</Nullable>\r\n  </PropertyGroup>\r\n</Project>\r\n";

    private static readonly AppVersion Current = new(0, 3, 0);

    [Fact]
    public void CurrentAndSuggestNext()
    {
        Assert.Equal(Current, ReleaseVersion.Current(Csproj));
        Assert.Equal(new AppVersion(0, 3, 1), ReleaseVersion.SuggestNext(Current));
        Assert.Throws<InvalidDataException>(() => ReleaseVersion.Current("<Project />"));
    }

    [Fact]
    public void VersionInputIsTrimmedAndRejectsPrefix()
    {
        Assert.Null(ReleaseVersion.Problem(" 0.4.0 ", Current, localFeed: false, out var version));
        Assert.Equal(new AppVersion(0, 4, 0), version);

        foreach (var bad in new[] { "v0.4.0", "0.4", "0.4.0-beta", string.Empty })
        {
            Assert.Contains("x.y.z", ReleaseVersion.Problem(bad, Current, localFeed: false, out _));
        }

        Assert.Contains("größer", ReleaseVersion.Problem("0.3.0", Current, localFeed: false, out _));
        Assert.Null(ReleaseVersion.Problem("0.3.0", Current, localFeed: true, out _));
    }

    [Fact]
    public void WithVersionReplacesOnlyTheVersionAndKeepsTheRest()
    {
        var updated = ReleaseVersion.WithVersion(Csproj, Current, new AppVersion(0, 4, 0));

        Assert.Equal(Csproj.Replace("<Version>0.3.0</Version>", "<Version>0.4.0</Version>"), updated);
        Assert.Throws<InvalidDataException>(() => ReleaseVersion.WithVersion(Csproj, new AppVersion(0, 2, 0), new AppVersion(0, 4, 0)));
    }
}
