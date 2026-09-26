using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// Update mode (<c>--apply-update</c>): runs from the staging folder of the new version, shows only a progress window and
/// swaps the program folder (see <see cref="UpdateRunner"/>). There is deliberately no path with administrator rights.
/// Ported from PMT's UpdateApplyMode.
/// </summary>
internal static class UpdateApplyMode
{
    private const int InvalidCallExitCode = 3;

    public static int Run(string[] args)
    {
        var log = new UpdateLog(UpdatePaths.Default.LogFile);
        var arguments = ApplyUpdateArguments.Parse(args);
        if (arguments is null)
        {
            log.Write("Ungültiger Aufruf des Update-Modus: " + CommandLine.Join(args));
            MessageBox.Show(UpdateTexts.InvalidArguments, UpdateTexts.ApplyTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            return InvalidCallExitCode;
        }

        // The staging folder has no language.json; show the window in the language of the installed program.
        Loc.UseLanguageOf(arguments.Target);

        var outcome = ApplyOutcome.RollbackFailed;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            outcome = await RunWithWindowAsync(arguments, log);
            app.Shutdown();
        };
        app.Run();
        return (int)outcome;
    }

    private static async Task<ApplyOutcome> RunWithWindowAsync(ApplyUpdateArguments arguments, UpdateLog log)
    {
        using var cancellation = new CancellationTokenSource();
        var window = new ProgressWindow(UpdateTexts.ApplyTitle, UpdateTexts.ApplyVersions(arguments.FromVersion, arguments.ToVersion), UpdateTexts.Cancel, cancellation.Cancel)
        {
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        window.Show();

        var staging = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var runner = new UpdateRunner(arguments, staging, UpdatePaths.Default, log, new WindowsProcessControl(), new UpdateApplier(log), UpdateRunnerTimings.Default);
        ApplyOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(new WindowUi(window), cancellation.Token);
        }
        catch (Exception ex)
        {
            // Last safety net: an unexpected error must not leave the user without a message.
            log.Write("Unerwarteter Fehler im Update-Modus: " + ex);
            outcome = ApplyOutcome.RollbackFailed;
        }
        finally
        {
            window.CloseAfterFinished();
        }

        if (outcome == ApplyOutcome.RollbackFailed)
        {
            MessageBox.Show(UpdateTexts.RollbackFailed(Path.Combine(arguments.Target, UpdatePaths.BackupFolderName), log.Path), UpdateTexts.ApplyTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        return outcome;
    }

    private sealed class WindowUi(ProgressWindow window) : IUpdateRunnerUi
    {
        public void Status(UpdateRunnerPhase phase, int waitingFor) => window.Report(UpdateTexts.Phase(phase, waitingFor), 0, 0);

        public Task<bool> KeepWaitingAsync(int count) =>
            Task.FromResult(MessageBox.Show(window, UpdateTexts.KeepWaiting(count), UpdateTexts.KeepWaitingTitle, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes);
    }
}
