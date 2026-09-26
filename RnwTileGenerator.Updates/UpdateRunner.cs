using System.ComponentModel;
using System.Diagnostics;

namespace RnwTileGenerator.Updates;

/// <summary>Prozesse für den Update-Modus; in Tests ersetzt.</summary>
public interface IProcessControl
{
    /// <summary>RNW-Prozesse, deren Programmdatei in <paramref name="folder"/> liegt (ohne den eigenen).</summary>
    IReadOnlyList<int> FindInFolder(string folder, int ownPid);

    bool HasExited(int pid);

    void Kill(int pid);

    /// <summary>Startet ohne erhöhte Rechte und liefert die Prozess-ID.</summary>
    int Start(string exe, IReadOnlyList<string> args);
}

public enum UpdateRunnerPhase
{
    Waiting,
    Swapping,
    Starting,
    Checking,
    RollingBack,
}

public interface IUpdateRunnerUi
{
    void Status(UpdateRunnerPhase phase, int waitingFor);

    /// <summary><see langword="false"/> = Update abbrechen.</summary>
    Task<bool> KeepWaitingAsync(int count);
}

public enum ApplyOutcome
{
    Applied = 0,
    Cancelled = 1,
    RolledBack = 2,
    RollbackFailed = 3,
}

public sealed record UpdateRunnerTimings(TimeSpan Poll, TimeSpan AskAfter, TimeSpan HealthTimeout)
{
    public static UpdateRunnerTimings Default { get; } = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90));
}

/// <summary>
/// Ablauf des Update-Modus in der neuen Exe: warten, bis kein RNW aus dem Programmordner mehr läuft; tauschen;
/// neue Version starten; auf ihr Startsignal warten; sonst zurückfallen und die alte Version starten. Läuft auf dem UI-Thread
/// (kein ConfigureAwait(false)), damit die Rückrufe an <see cref="IUpdateRunnerUi"/> dort ankommen; Dateiarbeit läuft über Task.Run.
/// </summary>
public sealed class UpdateRunner(
    ApplyUpdateArguments arguments,
    string staging,
    UpdatePaths paths,
    UpdateLog log,
    IProcessControl processes,
    UpdateApplier applier,
    UpdateRunnerTimings timings,
    Func<string, bool>? canWrite = null)
{
    private enum StepResult
    {
        Done,
        RolledBack,
        RollbackFailed,
    }

    private readonly Func<string, bool> _canWrite = canWrite ?? UpdateApplier.CanWrite;

    private string TargetExecutable => Path.Combine(arguments.Target, UpdatePaths.ExecutableName);

    public async Task<ApplyOutcome> RunAsync(IUpdateRunnerUi ui, CancellationToken cancellationToken)
    {
        log.Write($"Update-Modus: {arguments.FromVersion} -> {arguments.ToVersion} in {arguments.Target}");

        // Zwei Update-Vorgänge für denselben Ordner würden sich gegenseitig die Sicherung löschen.
        using var updateLock = UpdateLock.TryAcquire(paths, arguments.Target);
        if (updateLock is null)
        {
            log.Write("Für diesen Programmordner läuft schon ein Update, dieser Vorgang endet ohne Änderung.");
            return ApplyOutcome.Cancelled;
        }

        if (!await WaitForProcessesAsync(ui, cancellationToken))
        {
            log.Write("Abgebrochen beim Warten auf offene RNW-Fenster.");
            StartOld(UpdateStartArguments.ForCancelled(arguments.OpenProject, arguments.ToVersion));
            return ApplyOutcome.Cancelled;
        }

        // Kein Update mit Administratorrechten: Der Staging-Ordner liegt im Benutzerprofil und ist für jedes Programm des Nutzers
        // beschreibbar; ihn erhöht auszuführen, hieße fremden Code mit Adminrechten laufen zu lassen. Das alte RNW prüft das schon vor
        // dem Download; hier nur als letzte Sicherung.
        if (!_canWrite(arguments.Target))
        {
            log.Write("Kein Schreibrecht im Programmordner, Update ohne Änderung abgebrochen.");
            StartOld(UpdateStartArguments.ForCancelled(arguments.OpenProject, arguments.ToVersion));
            return ApplyOutcome.Cancelled;
        }

        ui.Status(UpdateRunnerPhase.Swapping, 0);
        switch (await SwapAsync())
        {
            case StepResult.RolledBack:
                StartOld(UpdateStartArguments.ForFailed(arguments.OpenProject, arguments.ToVersion));
                return ApplyOutcome.RolledBack;
            case StepResult.RollbackFailed:
                return ApplyOutcome.RollbackFailed;
        }

        ui.Status(UpdateRunnerPhase.Starting, 0);
        var marker = paths.Marker(arguments.ToVersion);
        TryDeleteFile(marker);
        var pid = TryStart(UpdateStartArguments.ForApplied(arguments.OpenProject, arguments.ToVersion, arguments.FromVersion));
        if (pid > 0)
        {
            ui.Status(UpdateRunnerPhase.Checking, 0);
            if (await WaitForMarkerAsync(pid, marker))
            {
                log.Write("Neue Version läuft, Update fertig.");
                return ApplyOutcome.Applied;
            }

            log.Write("Kein Startsignal der neuen Version, Rückfall.");
            processes.Kill(pid);
            await WaitForExitAsync(pid, TimeSpan.FromSeconds(10));
        }

        ui.Status(UpdateRunnerPhase.RollingBack, 0);
        if (!await RollbackAsync())
        {
            return ApplyOutcome.RollbackFailed;
        }

        StartOld(UpdateStartArguments.ForFailed(arguments.OpenProject, arguments.ToVersion));
        return ApplyOutcome.RolledBack;
    }

    private async Task<bool> WaitForProcessesAsync(IUpdateRunnerUi ui, CancellationToken cancellationToken)
    {
        var sinceQuestion = Stopwatch.StartNew();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            var count = CountRunning();
            if (count == 0)
            {
                return true;
            }

            ui.Status(UpdateRunnerPhase.Waiting, count);
            if (sinceQuestion.Elapsed >= timings.AskAfter)
            {
                if (!await ui.KeepWaitingAsync(count))
                {
                    return false;
                }

                sinceQuestion.Restart();
            }

            try
            {
                await Task.Delay(timings.Poll, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private int CountRunning()
    {
        var others = processes.FindInFolder(arguments.Target, Environment.ProcessId);
        var count = others.Count;
        if (!processes.HasExited(arguments.WaitPid) && !others.Contains(arguments.WaitPid))
        {
            count++;
        }

        return count;
    }

    private async Task<StepResult> SwapAsync()
    {
        try
        {
            await Task.Run(() => applier.Swap(arguments.Target, staging));
            return StepResult.Done;
        }
        catch (UpdateApplyException ex)
        {
            return ex.RolledBack ? StepResult.RolledBack : StepResult.RollbackFailed;
        }
    }

    private async Task<bool> RollbackAsync()
    {
        try
        {
            await Task.Run(() => applier.Rollback(arguments.Target));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Rückfall fehlgeschlagen: " + ex.Message);
            return false;
        }
    }

    /// <summary>Wartet auf das Startsignal; endet der neue Prozess vorher, sofort Schluss (kein Warten bis zur Frist).</summary>
    private async Task<bool> WaitForMarkerAsync(int pid, string marker)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timings.HealthTimeout)
        {
            if (File.Exists(marker))
            {
                return true;
            }

            if (processes.HasExited(pid))
            {
                return File.Exists(marker);
            }

            await Task.Delay(timings.Poll);
        }

        return File.Exists(marker);
    }

    private async Task WaitForExitAsync(int pid, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!processes.HasExited(pid) && clock.Elapsed < limit)
        {
            await Task.Delay(timings.Poll);
        }
    }

    private int TryStart(IReadOnlyList<string> startArguments)
    {
        try
        {
            var pid = processes.Start(TargetExecutable, startArguments);
            log.Write($"Gestartet ({pid}): {CommandLine.Join(startArguments)}");
            return pid;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log.Write("Start fehlgeschlagen: " + ex.Message);
            return -1;
        }
    }

    private void StartOld(IReadOnlyList<string> startArguments) => TryStart(startArguments);

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
