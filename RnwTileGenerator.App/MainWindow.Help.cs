using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// Help menu, update bar and the update install flow (check, download + verify, save question, start the update
/// mode). Ported from PMT's MainWindow.Help; RNW has no recovery data, so the user is asked to save the tile first
/// (see UpdateRestart).
/// </summary>
public sealed partial class MainWindow
{
    private UpdateSettings _updateSettings = UpdateSettings.Load(UpdateSettings.DefaultPath);
    private ReleaseInfo? _pendingRelease;
    private PreparedUpdate? _preparedUpdate;
    private bool _updateCheckRunning;
    private bool _updateInstallRunning;

    private Border? _updateBar;
    private TextBlock? _updateBarText;
    private Button? _updateBarInstall, _updateBarRelease, _updateBarSkip, _updateBarClose;

    // -- window layout --------------------------------------------------------

    /// <summary>Menu on top, the update bar right below it (hidden unless there is something to say), then the screen.</summary>
    private void SetScreen(Menu menu, UIElement body)
    {
        var bar = UpdateBar;
        (bar.Parent as Panel)?.Children.Remove(bar);
        RefreshUpdateBarTexts();

        var shell = new DockPanel();
        menu.SetValue(DockPanel.DockProperty, Dock.Top);
        shell.Children.Add(menu);
        bar.SetValue(DockPanel.DockProperty, Dock.Top);
        shell.Children.Add(bar);
        shell.Children.Add(body);
        Content = shell;
    }

    private Border UpdateBar => _updateBar ??= BuildUpdateBar();

    private Border BuildUpdateBar()
    {
        Button MakeButton(RoutedEventHandler onClick)
        {
            var b = new Button { Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
            b.Click += onClick;
            return b;
        }

        _updateBarText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _updateBarInstall = MakeButton(async (_, _) => { if (_pendingRelease is { } release) await InstallUpdateAsync(release); });
        _updateBarRelease = MakeButton((_, _) => { if (_pendingRelease is { } release) OpenReleasePage(release.HtmlUrl); });
        _updateBarSkip = MakeButton((_, _) =>
        {
            if (_pendingRelease is { } release) SaveUpdateSettings(_updateSettings with { SkippedUpdateVersion = release.Version.ToString() });
            HideUpdateBar();
        });
        _updateBarClose = MakeButton((_, _) => HideUpdateBar());
        _updateBarClose.Content = "✕";

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { _updateBarInstall, _updateBarRelease, _updateBarSkip, _updateBarClose }) buttons.Children.Add(b);
        var row = new DockPanel { Margin = new Thickness(8, 4, 8, 4) };
        buttons.SetValue(DockPanel.DockProperty, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(_updateBarText);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(255, 244, 206)),
            BorderBrush = Brushes.Goldenrod,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
            Visibility = Visibility.Collapsed,
        };
    }

    private void RefreshUpdateBarTexts()
    {
        _ = UpdateBar;
        _updateBarInstall!.Content = UpdateTexts.BarInstall;
        _updateBarInstall.ToolTip = UpdateTexts.BarInstallTip;
        _updateBarRelease!.Content = UpdateTexts.BarReleasePage;
        _updateBarRelease.ToolTip = UpdateTexts.BarReleasePageTip;
        _updateBarSkip!.Content = UpdateTexts.BarSkip;
        _updateBarClose!.ToolTip = UpdateTexts.BarLaterTip;
        if (_pendingRelease is { } release && _updateBarInstall.Visibility == Visibility.Visible)
            _updateBarText!.Text = UpdateTexts.BarAvailable(release.Version, AppInfo.Version);
    }

    private void ShowUpdateBar(ReleaseInfo release)
    {
        _pendingRelease = release;
        _ = UpdateBar;
        SetUpdateBarButtonsVisible(true);
        _updateBarText!.Text = UpdateTexts.BarAvailable(release.Version, AppInfo.Version);
        UpdateBar.Visibility = Visibility.Visible;
    }

    /// <summary>A plain notice ("Updated to X"): only the ✕ button.</summary>
    private void ShowUpdateNotice(string text)
    {
        _ = UpdateBar;
        SetUpdateBarButtonsVisible(false);
        _updateBarText!.Text = text;
        UpdateBar.Visibility = Visibility.Visible;
    }

    private void HideUpdateBar() => UpdateBar.Visibility = Visibility.Collapsed;

    private void SetUpdateBarButtonsVisible(bool visible)
    {
        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _updateBarInstall!.Visibility = visibility;
        _updateBarRelease!.Visibility = visibility;
        _updateBarSkip!.Visibility = visibility;
    }

    // -- Help menu ------------------------------------------------------------

    private MenuItem BuildHelpMenu()
    {
        var help = new MenuItem { Header = Loc.T("help.menu") };

        var check = new MenuItem { Header = Loc.T("help.checkUpdates"), ToolTip = Loc.T("help.checkUpdates.tip") };
        check.Click += async (_, _) => await CheckForUpdatesNowAsync();
        var atStartup = new MenuItem { Header = Loc.T("help.checkAtStartup"), ToolTip = Loc.T("help.checkAtStartup.tip"), IsCheckable = true, IsChecked = _updateSettings.CheckForUpdatesOnStartup };
        atStartup.Click += (_, _) => SaveUpdateSettings(_updateSettings with { CheckForUpdatesOnStartup = atStartup.IsChecked });

        var reportBug = new MenuItem { Header = Loc.T("help.reportBug") };
        reportBug.Click += (_, _) => new BugReportWindow(this).ShowDialog();

        var changelogPath = ReadmeFiles.ChangelogIn(AppContext.BaseDirectory);
        var changelog = new MenuItem { Header = Loc.T("help.changelog") };
        if (File.Exists(changelogPath))
        {
            changelog.Click += (_, _) => OpenWithShell(changelogPath);
        }
        else
        {
            changelog.IsEnabled = false;
            changelog.ToolTip = Loc.T("help.changelog.missing");
            ToolTipService.SetShowOnDisabled(changelog, true);
        }

        var about = new MenuItem { Header = Loc.T("help.about") };
        about.Click += (_, _) =>
        {
#if DEBUG
            // Test hook for the crash dialog's "Report bug" button (Debug builds only).
            if (Environment.GetEnvironmentVariable("RNW_DEBUG_CRASH") == "1")
                throw new InvalidOperationException("Debug crash (RNW_DEBUG_CRASH=1) while reading " + Path.Combine(AppContext.BaseDirectory, "test.rnwproj"));
#endif
            new AboutWindow(this).ShowDialog();
        };

        help.Items.Add(check);
        help.Items.Add(atStartup);
        help.Items.Add(new Separator());
        help.Items.Add(reportBug);
        help.Items.Add(changelog);
        help.Items.Add(about);
        return help;
    }

    private void SaveUpdateSettings(UpdateSettings settings)
    {
        _updateSettings = settings;
        settings.Save(UpdateSettings.DefaultPath);
    }

    // -- check ----------------------------------------------------------------

    /// <summary>After the first render: at most once a day, silent unless a (not skipped) newer version exists.</summary>
    private async void StartBackgroundUpdateCheck()
    {
        if (!_updateSettings.CheckForUpdatesOnStartup || !UpdateSchedule.IsDue(_updateSettings.LastUpdateCheckUtc, DateTime.UtcNow)) return;
        var result = await RunUpdateCheckAsync();
        if (result is { Status: UpdateCheckStatus.UpdateAvailable, Release: { } release } && !IsSkipped(release))
            ShowUpdateBar(release);
    }

    private async Task<UpdateCheckResult> RunUpdateCheckAsync()
    {
        _updateCheckRunning = true;
        try
        {
            var result = await new UpdateChecker(UpdateClients.Check, AppInfo.Repository).CheckAsync(AppInfo.Version);
            // Only successful checks count for the daily rhythm; after an error the next start tries again.
            if (result.Status != UpdateCheckStatus.Failed)
                SaveUpdateSettings(_updateSettings with { LastUpdateCheckUtc = DateTime.UtcNow });
            return result;
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    private bool IsSkipped(ReleaseInfo release) =>
        AppVersion.TryParse(_updateSettings.SkippedUpdateVersion, out var skipped) && release.Version <= skipped;

    private async Task CheckForUpdatesNowAsync()
    {
        if (_updateCheckRunning) return;
        var result = await RunUpdateCheckAsync();
        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Release is { } release:
                ShowUpdateBar(release);
                var answer = MessageBox.Show(this, UpdateTexts.AvailableQuestion(release.Version, AppInfo.Version), UpdateTexts.AvailableTitle, MessageBoxButton.YesNoCancel, MessageBoxImage.Information);
                if (answer == MessageBoxResult.Yes) await InstallUpdateAsync(release);
                else if (answer == MessageBoxResult.No) OpenReleasePage(release.HtmlUrl);
                break;

            case UpdateCheckStatus.UpToDate:
                MessageBox.Show(this, result.Release is null ? UpdateTexts.NoRelease : UpdateTexts.UpToDate(AppInfo.Version), UpdateTexts.CheckTitle);
                break;

            default:
                MessageBox.Show(this, UpdateTexts.CheckFailed(result.Error), UpdateTexts.CheckTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
        }
    }

    // -- install --------------------------------------------------------------

    private async Task InstallUpdateAsync(ReleaseInfo release)
    {
        if (_updateInstallRunning) return;
        _updateInstallRunning = true;
        try
        {
            // Without the file list (development folder, "dotnet run", copied together by hand) the swap would not know what belongs to RNW.
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, UpdatePaths.InstallListName)))
            {
                OfferReleasePage(release, UpdateTexts.NoInstallList);
                return;
            }

            // Protected folder (e.g. Program Files): no update with administrator rights, see UpdateRunner. Checked before the download.
            var installFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            if (!UpdateApplier.CanWrite(installFolder))
            {
                OfferReleasePage(release, UpdateTexts.ProtectedFolder(installFolder));
                return;
            }

            var prepared = _preparedUpdate is { } known && known.Version == release.Version && Directory.Exists(known.StagingFolder)
                ? known
                : await PrepareWithProgressAsync(release);
            if (prepared is null) return;
            _preparedUpdate = prepared;

            // Another window already started the update mode for this folder; a second one would delete its backup.
            if (UpdateLock.IsHeld(UpdatePaths.Default, installFolder))
            {
                MessageBox.Show(this, UpdateTexts.AlreadyRunning, UpdateTexts.NotPossibleTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var choice = UpdateReadyDialog.Ask(this, prepared.Version, Project != null);
            string? openProject = null;
            var action = UpdateRestart.Decide(choice, Project != null, () => openProject = TrySaveProjectForUpdate());
            if (action == UpdateRestartAction.Abort) return;

            if (LaunchUpdater(prepared, action == UpdateRestartAction.LaunchWithProject ? openProject : null))
                Application.Current.Shutdown();
        }
        finally
        {
            _updateInstallRunning = false;
        }
    }

    private async Task<PreparedUpdate?> PrepareWithProgressAsync(ReleaseInfo release)
    {
        using var cancellation = new CancellationTokenSource();
        var window = new ProgressWindow(UpdateTexts.DownloadTitle, UpdateTexts.DownloadHint, UpdateTexts.Cancel, cancellation.Cancel) { Owner = this };
        var status = UpdateTexts.DownloadStatus(release.Version);
        var progress = new Progress<DownloadProgress>(p =>
            window.Report(UpdateTexts.DownloadCount(status, ToMegabytes(p.Done), ToMegabytes(p.Total)), p.Done, p.Total));
        window.Show();
        PrepareResult result;
        try
        {
            var log = new UpdateLog(UpdatePaths.Default.LogFile);
            var preparer = new UpdatePreparer(new UpdateDownloader(UpdateClients.Download), UpdatePaths.Default, UpdateKeys.ProductionPublicKey, log);
            result = await preparer.PrepareAsync(release, AppInfo.Version, progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            window.CloseAfterFinished();
        }

        if (result.Update is { } update) return update;
        OfferReleasePage(release, UpdateTexts.Problem(result.Problem, result.Detail));
        return null;
    }

    private static int ToMegabytes(long bytes) => (int)(bytes / (1024 * 1024));

    /// <summary>Saves the tile before the restart (no "saved" message). Returns the path, or null when cancelled or failed.</summary>
    private string? TrySaveProjectForUpdate()
    {
        if (Project == null) return null;
        var path = CurrentPath;
        if (path == null)
        {
            var sfd = new SaveFileDialog
            {
                Title = "Save project as",
                DefaultExt = ".rnwproj",
                Filter = "RNW Tile Generator project (*.rnwproj)|*.rnwproj",
                FileName = $"{Project.Name}.rnwproj",
            };
            if (sfd.ShowDialog(this) != true) return null;
            path = sfd.FileName;
        }

        try
        {
            Project.Save(path);
            CurrentPath = path;
            return path;
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, "Save project", $"{exc.Message}\n\n{exc}");
            return null;
        }
    }

    /// <summary>Starts the new exe from the staging folder in update mode. True when it started.</summary>
    private bool LaunchUpdater(PreparedUpdate update, string? openProject)
    {
        var log = new UpdateLog(UpdatePaths.Default.LogFile);
        var arguments = new ApplyUpdateArguments(
            ApplyUpdateArguments.CurrentProtocol,
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            Environment.ProcessId,
            AppInfo.Version,
            update.Version,
            openProject);
        try
        {
            new WindowsProcessControl().Start(Path.Combine(update.StagingFolder, UpdatePaths.ExecutableName), arguments.ToArguments());
            log.Write($"Update-Modus gestartet: {AppInfo.Version} -> {update.Version}");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log.Write("Update-Modus nicht startbar: " + ex.Message);
            MessageBox.Show(this, UpdateTexts.LaunchFailed(ex.Message, log.Path), UpdateTexts.ApplyTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void OfferReleasePage(ReleaseInfo release, string reason)
    {
        if (MessageBox.Show(this, UpdateTexts.OfferReleasePage(reason), UpdateTexts.NotPossibleTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            OpenReleasePage(release.HtmlUrl);
    }

    /// <summary>Only GitHub addresses: the address comes from a network answer and is not handed blindly to the shell.</summary>
    private void OpenReleasePage(string url)
    {
        if (!ReleaseParser.IsGitHubUrl(url))
        {
            MessageBox.Show(this, UpdateTexts.NotGitHub, UpdateTexts.NotOpenedTitle);
            return;
        }

        OpenWithShell(url);
    }

    private void OpenWithShell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, UpdateTexts.BrowserFailed(ex.Message), UpdateTexts.ErrorTitle);
        }
    }
}
