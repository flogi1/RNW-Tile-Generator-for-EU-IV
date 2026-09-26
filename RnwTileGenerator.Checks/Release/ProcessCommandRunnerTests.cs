using RnwTileGenerator.Release;
using System.Diagnostics;

namespace RnwTileGenerator.Checks;

public class ProcessCommandRunnerTests
{
    [Fact]
    public async Task ProcessRunnerCapturesOutputAndExitCode()
    {
        var log = new List<string>();

        var result = await new ProcessCommandRunner().RunAsync(
            "cmd.exe", ["/c", "echo eins& echo zwei& exit /b 3"], Path.GetTempPath(), null, log.Add, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal(new[] { "eins", "zwei" }, result.Output);
        Assert.Contains("eins", log);
        Assert.Contains("zwei", log);
    }

    [Fact]
    public async Task ProcessRunnerPassesEnvironment()
    {
        var result = await new ProcessCommandRunner().RunAsync(
            "cmd.exe", ["/c", "echo %PMT_RELEASE_TEST%"], Path.GetTempPath(), new Dictionary<string, string> { ["PMT_RELEASE_TEST"] = "wert" }, _ => { }, CancellationToken.None);

        Assert.Equal(new[] { "wert" }, result.Output);
    }

    [Fact]
    public async Task ProcessRunnerKillsTheTreeOnCancel()
    {
        // Kindprozess waitfor.exe: andere Testprojekte starten parallel eigene ping-Prozesse, waitfor nutzt keiner.
        var before = Process.GetProcessesByName("waitfor").Select(p => p.Id).ToHashSet();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessCommandRunner().RunAsync(
            "cmd.exe", ["/c", $"waitfor /t 30 pmtRelease{Guid.NewGuid():N}"], Path.GetTempPath(), null, _ => { }, cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        var left = Process.GetProcessesByName("waitfor").Where(p => !before.Contains(p.Id)).ToList();
        Assert.All(left, p => Assert.True(p.WaitForExit(3000), $"waitfor {p.Id} läuft noch"));
    }
}
