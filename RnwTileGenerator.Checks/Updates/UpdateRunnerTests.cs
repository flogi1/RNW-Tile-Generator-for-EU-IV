using System.Diagnostics;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class UpdateRunnerTests : IDisposable
{
    private static readonly AppVersion From = new(1, 1, 0);
    private static readonly AppVersion To = new(1, 2, 0);
    private const string Project = @"D:\Tiles\Mein Tile.rnwproj";
    private static readonly UpdateRunnerTimings Fast = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(300));

    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Target => _temp.Combine("Programm");

    private string Staging => _temp.Combine("staging");

    private UpdatePaths Paths => new(_temp.Combine("updates"));

    private sealed class FakeProcesses : IProcessControl
    {
        public List<int> Running { get; } = [];
        public List<(string Exe, IReadOnlyList<string> Args)> Started { get; } = [];
        public List<int> Killed { get; } = [];
        public Func<int, bool> Exited { get; set; } = _ => true;
        public Action? OnStartNew { get; set; }

        public IReadOnlyList<int> FindInFolder(string folder, int ownPid) => Running.ToList();

        public bool HasExited(int pid) => Exited(pid);

        public void Kill(int pid) => Killed.Add(pid);

        public int Start(string exe, IReadOnlyList<string> args)
        {
            Started.Add((exe, args));
            if (args.Contains(UpdateStartArguments.Applied))
            {
                OnStartNew?.Invoke();
            }

            return 1000 + Started.Count;
        }
    }

    private sealed class FakeUi(bool keepWaiting) : IUpdateRunnerUi
    {
        public List<UpdateRunnerPhase> Phases { get; } = [];
        public int Questions { get; private set; }

        public void Status(UpdateRunnerPhase phase, int waitingFor)
        {
            if (Phases.Count == 0 || Phases[^1] != phase)
            {
                Phases.Add(phase);
            }
        }

        public Task<bool> KeepWaitingAsync(int count)
        {
            Questions++;
            return Task.FromResult(keepWaiting);
        }
    }

    private void CreateFolders()
    {
        _temp.WriteFile(Path.Combine("Programm", UpdatePaths.ExecutableName), "alt");
        _temp.WriteFile(Path.Combine("Programm", UpdatePaths.InstallListName), UpdatePaths.ExecutableName);
        _temp.WriteFile(Path.Combine("staging", UpdatePaths.ExecutableName), "neu");
        _temp.WriteFile(Path.Combine("staging", UpdatePaths.InstallListName), UpdatePaths.ExecutableName);
    }

    private void WriteMarker()
    {
        Directory.CreateDirectory(Paths.VersionFolder(To));
        File.WriteAllText(Paths.Marker(To), "ok");
    }

    private UpdateRunner Runner(FakeProcesses processes, UpdateApplier? applier = null, Func<string, bool>? canWrite = null)
    {
        var log = new UpdateLog(_temp.Combine("update.log"));
        var arguments = new ApplyUpdateArguments(1, Target, 42, From, To, Project);
        return new UpdateRunner(arguments, Staging, Paths, log, processes,
            applier ?? new UpdateApplier(log, retryDelay: TimeSpan.Zero), Fast, canWrite ?? (_ => true));
    }

    private string CurrentExe() => File.ReadAllText(Path.Combine(Target, UpdatePaths.ExecutableName));

    [Fact]
    public async Task Erfolgreicher_Ablauf_tauscht_startet_neu_und_wartet_auf_das_Startsignal()
    {
        CreateFolders();
        var processes = new FakeProcesses { OnStartNew = WriteMarker };
        var ui = new FakeUi(keepWaiting: true);

        var outcome = await Runner(processes).RunAsync(ui, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Applied, outcome);
        Assert.Equal("neu", CurrentExe());
        var (exe, args) = Assert.Single(processes.Started);
        Assert.Equal(Path.Combine(Target, UpdatePaths.ExecutableName), exe);
        Assert.Equal(UpdateStartArguments.ForApplied(Project, To, From), args);
        Assert.Equal(new[] { UpdateRunnerPhase.Swapping, UpdateRunnerPhase.Starting, UpdateRunnerPhase.Checking }, ui.Phases);
    }

    [Fact]
    public async Task Ohne_Startsignal_wird_die_neue_Version_beendet_und_die_alte_gestartet()
    {
        CreateFolders();
        var processes = new FakeProcesses();
        var newPid = 0;
        processes.Exited = pid => pid != newPid || processes.Killed.Contains(pid);
        processes.OnStartNew = () => newPid = 1001;

        var outcome = await Runner(processes).RunAsync(new FakeUi(true), CancellationToken.None);

        Assert.Equal(ApplyOutcome.RolledBack, outcome);
        Assert.Equal(new[] { 1001 }, processes.Killed);
        Assert.Equal("alt", CurrentExe());
        Assert.Equal(UpdateStartArguments.ForFailed(Project, To), processes.Started[^1].Args);
    }

    [Fact]
    public async Task Sofortiger_Absturz_faellt_ohne_Wartezeit_zurueck()
    {
        CreateFolders();
        var processes = new FakeProcesses { Exited = _ => true };
        var slowHealth = Fast with { HealthTimeout = TimeSpan.FromSeconds(30) };
        var log = new UpdateLog(_temp.Combine("update.log"));
        var runner = new UpdateRunner(new ApplyUpdateArguments(1, Target, 42, From, To, Project), Staging, Paths, log, processes,
            new UpdateApplier(log, retryDelay: TimeSpan.Zero), slowHealth, _ => true);
        var clock = Stopwatch.StartNew();

        var outcome = await runner.RunAsync(new FakeUi(true), CancellationToken.None);

        Assert.Equal(ApplyOutcome.RolledBack, outcome);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"dauerte {clock.Elapsed}");
        Assert.Equal("alt", CurrentExe());
    }

    [Fact]
    public async Task Offene_Fenster_und_Nein_bei_Weiter_warten_bricht_ohne_Aenderung_ab()
    {
        CreateFolders();
        var processes = new FakeProcesses();
        processes.Running.Add(7);
        var ui = new FakeUi(keepWaiting: false);

        var outcome = await Runner(processes).RunAsync(ui, CancellationToken.None);

        Assert.Equal(ApplyOutcome.Cancelled, outcome);
        Assert.Equal(1, ui.Questions);
        Assert.Equal("alt", CurrentExe());
        Assert.Equal(UpdateStartArguments.ForCancelled(Project, To), Assert.Single(processes.Started).Args);
    }

    [Fact]
    public async Task Warten_auf_den_alten_Prozess_und_Abbrechen_per_Knopf()
    {
        CreateFolders();
        var processes = new FakeProcesses { Exited = pid => pid != 42 };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        var outcome = await Runner(processes).RunAsync(new FakeUi(true), cancellation.Token);

        Assert.Equal(ApplyOutcome.Cancelled, outcome);
        Assert.Equal("alt", CurrentExe());
    }

    [Fact]
    public async Task Fehler_beim_Tausch_startet_die_alte_Version_mit_Fehlermeldung()
    {
        CreateFolders();
        var processes = new FakeProcesses();
        var log = new UpdateLog(_temp.Combine("update.log"));
        var applier = new UpdateApplier(log, retryDelay: TimeSpan.Zero) { BeforeCopyFile = _ => throw new IOException("kaputt") };

        var outcome = await Runner(processes, applier).RunAsync(new FakeUi(true), CancellationToken.None);

        Assert.Equal(ApplyOutcome.RolledBack, outcome);
        Assert.Equal("alt", CurrentExe());
        Assert.Equal(UpdateStartArguments.ForFailed(Project, To), Assert.Single(processes.Started).Args);
    }

    [Fact]
    public async Task Zweiter_Update_Vorgang_fuer_denselben_Ordner_tut_nichts()
    {
        CreateFolders();
        var processes = new FakeProcesses { OnStartNew = WriteMarker };
        using var running = UpdateLock.TryAcquire(Paths, Target);

        var outcome = await Runner(processes).RunAsync(new FakeUi(true), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Cancelled, outcome);
        Assert.Empty(processes.Started);
        Assert.Equal("alt", CurrentExe());
    }

    [Fact]
    public async Task Ohne_Schreibrecht_bricht_das_Update_ohne_Aenderung_und_ohne_Adminrechte_ab()
    {
        CreateFolders();
        var processes = new FakeProcesses { OnStartNew = WriteMarker };

        var outcome = await Runner(processes, canWrite: _ => false).RunAsync(new FakeUi(true), CancellationToken.None);

        Assert.Equal(ApplyOutcome.Cancelled, outcome);
        Assert.Equal("alt", CurrentExe());
        Assert.Equal(UpdateStartArguments.ForCancelled(Project, To), Assert.Single(processes.Started).Args);
    }
}
