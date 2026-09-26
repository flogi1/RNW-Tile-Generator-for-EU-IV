using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

/// <summary>Ported from PMT's CommandLineTests; PMT's --game/--lang became RNW's --open (project to reopen).</summary>
public class CommandLineTests
{
    private const string Project = @"D:\Tiles\Mein Tile.rnwproj";

    [Fact]
    public void Apply_arguments_roundtrip()
    {
        var original = new ApplyUpdateArguments(1, @"C:\Program Files\RNW Ä", 4711, new AppVersion(1, 1, 0), new AppVersion(1, 2, 0), Project);
        var parsed = ApplyUpdateArguments.Parse(WindowsCommandLine.Split(CommandLine.Join(original.ToArguments())));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void Arguments_roundtrip_with_spaces_and_umlauts()
    {
        var original = new ApplyUpdateArguments(1, @"C:\Users\Jörg\RNW Tile Generator", 12, new AppVersion(1, 1, 0), new AppVersion(1, 1, 1), Project);
        Assert.Equal(original, ApplyUpdateArguments.Parse(WindowsCommandLine.Split(CommandLine.Join(original.ToArguments()))));

        var start = UpdateStartArguments.ForApplied(Project, new AppVersion(1, 1, 1), new AppVersion(1, 1, 0));
        var info = UpdateStartArguments.Parse(WindowsCommandLine.Split(CommandLine.Join(start)));
        Assert.Equal(Project, info.OpenProject);
        Assert.Equal(new AppVersion(1, 1, 1), info.Applied);
    }

    [Fact]
    public void Apply_arguments_ignore_unknown_and_normalise_target()
    {
        var parsed = ApplyUpdateArguments.Parse([
            "--apply-update", "--neu-in-v7", "wert", "--protocol", "1", "--flag", "--target", @"C:\RNW\",
            "--wait-pid", "12", "--from-version", "1.0.0", "--to-version", "1.1.0"]);

        Assert.NotNull(parsed);
        Assert.Equal(@"C:\RNW", parsed.Target);
        Assert.Null(parsed.OpenProject);
    }

    [Theory]
    [InlineData("--protocol", "0")]
    [InlineData("--target", "relativ")]
    [InlineData("--wait-pid", "x")]
    [InlineData("--to-version", "neu")]
    public void Apply_arguments_reject_invalid_values(string name, string value)
    {
        var args = new Dictionary<string, string>
        {
            ["--protocol"] = "1", ["--target"] = @"C:\RNW", ["--wait-pid"] = "12", ["--from-version"] = "1.0.0", ["--to-version"] = "1.1.0",
        };
        args[name] = value;
        var list = new List<string> { ApplyUpdateArguments.Switch };
        foreach (var (key, text) in args)
        {
            list.Add(key);
            list.Add(text);
        }

        Assert.Null(ApplyUpdateArguments.Parse(list));
        Assert.Null(ApplyUpdateArguments.Parse(["--anders", "--protocol", "1"]));
    }

    [Fact]
    public void Start_arguments_for_the_normal_app()
    {
        var to = new AppVersion(1, 2, 0);
        var from = new AppVersion(1, 1, 0);

        var applied = UpdateStartArguments.ForApplied(Project, to, from);
        Assert.Equal(new[] { "--open", Project, "--update-applied", "1.2.0", "--update-from", "1.1.0" }, applied);
        Assert.Equal(new UpdateStartInfo(to, from, null, null, Project), UpdateStartArguments.Parse(applied));
        Assert.Equal(new UpdateStartInfo(null, null, to, null, null), UpdateStartArguments.Parse(UpdateStartArguments.ForFailed(null, to)));
        Assert.Equal(new UpdateStartInfo(null, null, null, to, Project), UpdateStartArguments.Parse(UpdateStartArguments.ForCancelled(Project, to)));

        var none = UpdateStartArguments.Parse(["--open", Project]);
        Assert.False(none.IsUpdateStart);
        Assert.Equal(Project, none.OpenProject);
        Assert.True(UpdateStartArguments.Parse(applied).IsUpdateStart);
    }

    [Fact]
    public void Update_arguments_are_stripped_from_other_argument_handling()
    {
        var applied = UpdateStartArguments.ForApplied(Project, new AppVersion(1, 2, 0), new AppVersion(1, 1, 0));
        Assert.Equal(new[] { "--open", Project }, UpdateStartArguments.WithoutUpdateArguments(applied));
    }
}
