using RnwTileGenerator.App;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class UpdateRestartChecks
{
    private int _saveCalls;

    private Func<string?> Save(string? result) => () => { _saveCalls++; return result; };

    [Fact]
    public void Cancel_aborts_without_saving()
    {
        Assert.Equal(UpdateRestartAction.Abort, UpdateRestart.Decide(UpdateRestartChoice.Cancel, hasProject: true, lastSavedPath: null, Save(@"D:\x.rnwproj")));
        Assert.Equal(0, _saveCalls);
    }

    [Fact]
    public void Save_cancelled_or_failed_aborts()
    {
        Assert.Equal(UpdateRestartAction.Abort, UpdateRestart.Decide(UpdateRestartChoice.SaveAndRestart, hasProject: true, lastSavedPath: null, Save(null)));
        Assert.Equal(1, _saveCalls);
    }

    [Fact]
    public void Save_ok_launches_with_project()
    {
        Assert.Equal(UpdateRestartAction.LaunchWithProject, UpdateRestart.Decide(UpdateRestartChoice.SaveAndRestart, hasProject: true, lastSavedPath: null, Save(@"D:\x.rnwproj")));
        Assert.Equal(1, _saveCalls);
    }

    [Fact]
    public void Without_saving_launches_without_project()
    {
        Assert.Equal(UpdateRestartAction.LaunchWithoutProject, UpdateRestart.Decide(UpdateRestartChoice.RestartWithoutSaving, hasProject: true, lastSavedPath: null, Save(@"D:\x.rnwproj")));
        Assert.Equal(0, _saveCalls);
    }

    [Fact]
    public void No_project_launches_without_project()
    {
        Assert.Equal(UpdateRestartAction.LaunchWithoutProject, UpdateRestart.Decide(UpdateRestartChoice.SaveAndRestart, hasProject: false, lastSavedPath: null, Save(@"D:\x.rnwproj")));
        Assert.Equal(0, _saveCalls);
    }
    [Fact]
    public void Without_saving_reopens_the_last_saved_file()
    {
        Assert.Equal(UpdateRestartAction.LaunchWithLastSaved,
            UpdateRestart.Decide(UpdateRestartChoice.RestartWithoutSaving, hasProject: true, lastSavedPath: @"D:\x.rnwproj", Save(@"D:\y.rnwproj")));
        Assert.Equal(0, _saveCalls);
    }

    [Fact]
    public void Readme_files_are_copied_next_to_exe()
    {
        var changelog = ReadmeFiles.ChangelogIn(AppContext.BaseDirectory);
        Assert.True(File.Exists(changelog), changelog);
        Assert.StartsWith("Unreleased", File.ReadAllText(changelog));
        foreach (var name in new[] { "LICENSE", "Installation Readme.txt", "RELEASE_NOTES.txt" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, name)), name);
    }
}
