using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using RnwTileGenerator.Core;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// Start after an update: start signal for the waiting update mode, messages for success / failure / cancel,
/// reopening the project given by --open. On a normal start (without update arguments) the backup and old
/// update folders are deleted in the background. Ported from PMT's MainWindow.UpdateStart.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>From Program.Main once the main window has been rendered for the first time.</summary>
    internal void OnFirstRenderedAfterStart()
    {
        var log = new UpdateLog(UpdatePaths.Default.LogFile);
        if (!_start.IsUpdateStart)
        {
            var installFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            _ = Task.Run(() => UpdateApplier.CleanupAfterStart(installFolder, UpdatePaths.Default, AppInfo.Version, log));
            StartBackgroundUpdateCheck();
        }

        if (_start.OpenProject is { } project && File.Exists(project))
        {
            OpenProjectFile(project);
        }

        // Only after the splash has closed: a MessageBox appearing at the same moment would vanish again at once.
        if (_start.Applied is { } applied)
        {
            ShowUpdateNotice(UpdateTexts.BarUpdated(applied));
        }

        if (_start.Failed is { } failed)
        {
            ShowAfterIdle(() => MessageBox.Show(this, UpdateTexts.StartFailed(failed, log.Path), UpdateTexts.StartFailedTitle, MessageBoxButton.OK, MessageBoxImage.Warning));
        }

        if (_start.Cancelled is { } cancelled)
        {
            ShowAfterIdle(() => MessageBox.Show(this, UpdateTexts.StartCancelled(cancelled), UpdateTexts.StartCancelledTitle, MessageBoxButton.OK, MessageBoxImage.Information));
        }
    }

    /// <summary>
    /// The signal the update mode waits for (up to 90 s). Written by Program.Main right after the main window is created,
    /// before it is shown: dialogs while loading must not use up the time limit. In Debug builds RNW_UPDATE_SKIP_MARKER=1
    /// suppresses it, to test the rollback.
    /// </summary>
    internal static void WriteStartMarker(AppVersion version)
    {
        var log = new UpdateLog(UpdatePaths.Default.LogFile);
#if DEBUG
        if (Environment.GetEnvironmentVariable("RNW_UPDATE_SKIP_MARKER") == "1")
        {
            log.Write("Startsignal absichtlich unterdrückt (RNW_UPDATE_SKIP_MARKER).");
            return;
        }
#endif
        try
        {
            var marker = UpdatePaths.Default.Marker(version);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            log.Write($"Startsignal für {version} geschrieben.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Startsignal nicht schreibbar: " + ex.Message);
        }
    }

    private void OpenProjectFile(string path)
    {
        try
        {
            StartEditor(TileProject.Load(path), path);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, "Could not load project", $"{exc.Message}\n\n{exc}");
        }
    }

    private void ShowAfterIdle(Action action) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, action);
}
