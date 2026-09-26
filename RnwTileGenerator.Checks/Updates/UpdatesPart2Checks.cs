using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class UpdatesPart2Checks : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>The files RNW writes next to its exe; they are never part of the install list.</summary>
    private static readonly string[] UserFiles =
        ["language.json", "generation_presets.json", "saved_seeds.json", "brush_presets.json", "generation_prefs.json", "crash_log.txt"];

    private string Target => _temp.Combine("RNW Tile Generator Ä");

    private string Staging => _temp.Combine("staging");

    private Dictionary<string, string> UserFileContents() => UserFiles.ToDictionary(name => name, name => File.ReadAllText(Path.Combine(Target, name)));

    private void CreateInstall()
    {
        _temp.WriteFile(Path.Combine("RNW Tile Generator Ä", UpdatePaths.InstallListName), "RnwTileGenerator.exe\nIcons\nReadme");
        _temp.WriteFile(Path.Combine("RNW Tile Generator Ä", UpdatePaths.ExecutableName), "alt");
        _temp.WriteFile(Path.Combine("RNW Tile Generator Ä", "Icons", "a.dds"), "alt");
        _temp.WriteFile(Path.Combine("RNW Tile Generator Ä", "Readme", "Changelog.txt"), "alt");
        foreach (var name in UserFiles)
            _temp.WriteFile(Path.Combine("RNW Tile Generator Ä", name), "user data of " + name);

        _temp.WriteFile(Path.Combine("staging", UpdatePaths.InstallListName), "RnwTileGenerator.exe\nIcons\nReadme");
        _temp.WriteFile(Path.Combine("staging", UpdatePaths.ExecutableName), "neu");
        _temp.WriteFile(Path.Combine("staging", "Icons", "a.dds"), "neu");
        _temp.WriteFile(Path.Combine("staging", "Readme", "Changelog.txt"), "neu");
    }

    [Fact]
    public void Swap_and_rollback_keep_user_files()
    {
        CreateInstall();
        var before = UserFileContents();
        var applier = new UpdateApplier(new UpdateLog(_temp.Combine("update.log")), retryDelay: TimeSpan.Zero);

        applier.Swap(Target, Staging);
        Assert.Equal(before, UserFileContents());
        Assert.Equal("neu", File.ReadAllText(Path.Combine(Target, UpdatePaths.ExecutableName)));

        applier.Rollback(Target);
        Assert.Equal(before, UserFileContents());
        Assert.Equal("alt", File.ReadAllText(Path.Combine(Target, UpdatePaths.ExecutableName)));
        Assert.Equal("alt", File.ReadAllText(Path.Combine(Target, "Icons", "a.dds")));
    }

    [Fact]
    public void Settings_load_missing_or_corrupt_gives_defaults()
    {
        var missing = UpdateSettings.Load(_temp.Combine("nope", "update_settings.json"));
        Assert.Equal(new UpdateSettings(), missing);
        Assert.True(missing.CheckForUpdatesOnStartup);

        var corrupt = _temp.WriteFile("update_settings.json", "{ kaputt");
        Assert.Equal(new UpdateSettings(), UpdateSettings.Load(corrupt));
    }

    [Fact]
    public void Settings_roundtrip()
    {
        var path = _temp.Combine("sub dir Ä", "update_settings.json");
        var settings = new UpdateSettings(false, new DateTime(2026, 9, 26, 10, 0, 0, DateTimeKind.Utc), "1.2.0");
        settings.Save(path);
        Assert.Equal(settings, UpdateSettings.Load(path));
    }

    [Fact]
    public void Settings_default_path_is_next_to_the_update_folder()
    {
        Assert.EndsWith(Path.Combine("RnwTileGenerator", "update_settings.json"), UpdateSettings.DefaultPath);
    }
}
