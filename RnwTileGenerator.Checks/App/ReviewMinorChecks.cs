using RnwTileGenerator.App;
using RnwTileGenerator.Release;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

/// <summary>Checks for the small follow-ups from the whole-branch review (after v1.0.7).</summary>
public sealed class ReviewMinorChecks : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose()
    {
        Loc.UseLanguage(AppLanguage.English);
        _temp.Dispose();
    }

    [Fact]
    public void Gh_locator_accepts_quoted_path_entries()
    {
        var gh = _temp.WriteFile(Path.Combine("GitHub CLI Ä", "gh.exe"), "exe");
        var folder = Path.GetDirectoryName(gh)!;

        Assert.Equal(gh, GhLocator.Find($"C:\\nowhere;\"{folder}\"", _temp.Combine("pf"), _temp.Combine("appdata")));
        Assert.Null(GhLocator.Find("C:\\nowhere", _temp.Combine("pf"), _temp.Combine("appdata")));
    }

    [Fact]
    public void Build_output_skips_excluded_folders_without_entering_them()
    {
        _temp.WriteFile(Path.Combine("RnwTileGenerator.App", "RnwTileGenerator.App.csproj"), "<Project />");
        _temp.WriteFile(Path.Combine("RnwTileGenerator.App", "bin", "x.dll"), "x");
        _temp.WriteFile(Path.Combine("tools", "RnwTileGenerator.Release", "RnwTileGenerator.Release.csproj"), "<Project />");
        _temp.WriteFile(Path.Combine("tools", "RnwTileGenerator.Release", "obj", "x.json"), "x");
        _temp.WriteFile(Path.Combine("loco", "RnwTileGenerator.App", "RnwTileGenerator.App.csproj"), "<Project />");
        _temp.WriteFile(Path.Combine("loco", "RnwTileGenerator.App", "bin", "keep.dll"), "x");
        _temp.WriteFile(Path.Combine("RNW Exports", "t", "RnwTileGenerator.App.csproj"), "<Project />");
        _temp.WriteFile(Path.Combine("RNW Exports", "t", "bin", "keep.dll"), "x");

        var folders = ReleaseRun.BuildOutputFolders(_temp.Path).Select(f => Path.GetRelativePath(_temp.Path, f)).Order().ToList();

        Assert.Equal(new[] { Path.Combine("RnwTileGenerator.App", "bin"), Path.Combine("tools", "RnwTileGenerator.Release", "obj") }, folders);
    }

    [Fact]
    public void User_icons_override_shipped_icons()
    {
        _temp.WriteFile(Path.Combine("Icons", "estuary.dds"), "shipped");
        _temp.WriteFile(Path.Combine("Icons", "paradise.dds"), "shipped");
        _temp.WriteFile(Path.Combine(IconCatalog.UserIconsFolder, "estuary.dds"), "user");

        Assert.Equal(_temp.Combine(IconCatalog.UserIconsFolder, "estuary.dds"), IconCatalog.ResolvePath("estuary", _temp.Path));
        Assert.Equal(_temp.Combine("Icons", "paradise.dds"), IconCatalog.ResolvePath("paradise", _temp.Path));
        Assert.Null(IconCatalog.ResolvePath("strait", _temp.Path));
    }

    [Fact]
    public void User_icons_folder_is_not_part_of_an_update()
    {
        Assert.DoesNotContain(IconCatalog.UserIconsFolder, File.ReadAllLines(_temp.WriteFile("list.txt", "RnwTileGenerator.exe\nIcons\nReadme")));
        Assert.NotEqual("Icons", IconCatalog.UserIconsFolder);
    }

    [Fact]
    public void Update_notice_text_follows_the_language()
    {
        var notices = new[]
        {
            new UpdateNotice(UpdateNoticeKind.Applied, new AppVersion(1, 0, 8)),
            new UpdateNotice(UpdateNoticeKind.Failed, new AppVersion(1, 0, 8)),
            new UpdateNotice(UpdateNoticeKind.Cancelled, new AppVersion(1, 0, 8)),
            new UpdateNotice(UpdateNoticeKind.Available, new AppVersion(1, 0, 8)),
        };
        foreach (var notice in notices)
        {
            Loc.UseLanguage(AppLanguage.English);
            var english = notice.Text(new AppVersion(1, 0, 7));
            Loc.UseLanguage(AppLanguage.German);
            var german = notice.Text(new AppVersion(1, 0, 7));
            Assert.Contains("1.0.8", english);
            Assert.NotEqual(english, german);
        }
    }

    [Fact]
    public void Only_available_and_failed_notices_have_extra_buttons()
    {
        Assert.True(new UpdateNotice(UpdateNoticeKind.Available, new AppVersion(1, 0, 8)).ShowsUpdateButtons);
        Assert.False(new UpdateNotice(UpdateNoticeKind.Applied, new AppVersion(1, 0, 8)).ShowsUpdateButtons);
        Assert.True(new UpdateNotice(UpdateNoticeKind.Failed, new AppVersion(1, 0, 8)).ShowsLogButton);
        Assert.False(new UpdateNotice(UpdateNoticeKind.Cancelled, new AppVersion(1, 0, 8)).ShowsLogButton);
    }
}
