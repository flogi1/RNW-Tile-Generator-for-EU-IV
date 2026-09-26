using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Release;

/// <summary>
/// Release-Fenster (Spec "Fenster und Ablauf"): links Version, Changelog und Test-Feed, rechts Schritte, Protokoll und Knöpfe. Die Logik liegt in
/// <see cref="ReleaseRun"/>; hier nur Verdrahtung und Sperren der Eingaben.
/// </summary>
public sealed class ReleaseWindow : Window
{
    private static readonly Dictionary<ReleaseStep, string> StepNames = new()
    {
        [ReleaseStep.Checks] = "Vorprüfungen",
        [ReleaseStep.Build] = "Bauen",
        [ReleaseStep.Tests] = "Checks (RnwTileGenerator.Checks)",
        [ReleaseStep.Publish] = "Publish",
        [ReleaseStep.Package] = "Paket",
        [ReleaseStep.Sign] = "Signieren und prüfen",
        [ReleaseStep.LocalFeed] = "Lokaler Feed",
        [ReleaseStep.Commit] = "Lokaler Commit",
        [ReleaseStep.PublicCommit] = "Öffentlicher Commit",
        [ReleaseStep.Push] = "Tag und Push",
        [ReleaseStep.GitHubRelease] = "GitHub-Release",
    };

    private readonly ReleasePaths _paths;
    private readonly AppVersion _current;
    private readonly string? _gh = GhLocator.Find();
    private readonly Dictionary<ReleaseStep, StepState> _states = Enum.GetValues<ReleaseStep>().ToDictionary(s => s, _ => StepState.Waiting);

    private readonly TextBox _version = new() { Width = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _versionError = Error();
    private readonly TextBox _changelog = new()
    {
        AcceptsReturn = true,
        AcceptsTab = true,
        FontFamily = new FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly TextBlock _changelogError = Error();
    private readonly CheckBox _localFeed = new() { Content = "Nur lokaler Test-Feed (Debug, ohne Git, Tests und Upload)", Margin = new Thickness(0, 8, 0, 4) };
    private readonly TextBox _feedFolder = new() { Width = 300 };
    private readonly Button _chooseFeed = NewButton("Ordner wählen");
    private readonly ListBox _steps = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Consolas"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private readonly Button _build = NewButton("Bauen und testen");
    private readonly Button _recheck = NewButton("Erneut prüfen");
    private readonly PasswordBox _password = new() { Width = 180, Margin = new Thickness(0, 0, 6, 0) };
    private readonly Button _sign = NewButton("Signieren und prüfen");
    private readonly Button _publish = NewButton("Veröffentlichen");
    private readonly Button _cancel = NewButton("Abbrechen");
    private readonly Button _retry = NewButton("Release-Seite nachholen");
    private readonly TextBlock _notice = new() { Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };

    private ReleaseRun? _run;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _built;
    private bool _signed;
    private bool _finished;
    private bool _closeWhenIdle;

    public ReleaseWindow()
    {
        Title = "RNW Release";
        Width = 1200;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        if (ReleasePaths.FindRoot(AppContext.BaseDirectory) is not { } root)
        {
            MessageBox.Show("RnwTileGenerator.sln nicht gefunden. Das Werkzeug muss im Repo-Ordner liegen (release-tool\\).", Title);
            _paths = new ReleasePaths(AppContext.BaseDirectory);
            Loaded += (_, _) => Close();
            return;
        }

        _paths = new ReleasePaths(root);
        _current = ReleaseVersion.Current(File.ReadAllText(_paths.AppProject));
        _version.Text = ReleaseVersion.SuggestNext(_current).ToString();
        _changelog.Text = File.Exists(_paths.Changelog) ? Changelog.UnreleasedBlock(File.ReadAllText(_paths.Changelog)) : Changelog.EmptyUnreleased;
        _feedFolder.Text = Path.Combine(Path.GetTempPath(), "rnw-feed");
        if (Environment.ProcessPath is { } exe && _paths.ToolSourcesNewerThan(File.GetLastWriteTimeUtc(exe)))
        {
            _notice.Text = "Das Werkzeug ist älter als seine Quellen. build-release-tool.bat erneut ausführen.";
        }

        Content = Layout();
        _version.TextChanged += (_, _) => Validate();
        _changelog.TextChanged += (_, _) => Validate();
        _localFeed.Click += async (_, _) => await CheckAsync();
        _chooseFeed.Click += (_, _) => ChooseFeed();
        _build.Click += async (_, _) => await BuildAsync();
        _recheck.Click += async (_, _) => await CheckAsync();
        _sign.Click += (_, _) => Sign();
        _publish.Click += async (_, _) => await PublishAsync();
        _retry.Click += async (_, _) => await RetryAsync();
        _cancel.Click += (_, _) => _cancellation?.Cancel();
        Closing += OnClosing;
        Loaded += async (_, _) => await CheckAsync();
        ShowSteps();
        Validate();
    }

    private bool IsLocal => _localFeed.IsChecked == true;

    private UIElement Layout()
    {
        var left = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel();
        top.Children.Add(_notice);
        top.Children.Add(new TextBlock { Text = $"Version (aktuell {_current})", FontWeight = FontWeights.Bold });
        top.Children.Add(_version);
        top.Children.Add(_versionError);
        top.Children.Add(new TextBlock { Text = "Changelog (Readme/Changelog.txt, Block \"Unreleased\")", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 2) });
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        var bottom = new StackPanel();
        bottom.Children.Add(_changelogError);
        bottom.Children.Add(_localFeed);
        var feed = new StackPanel { Orientation = Orientation.Horizontal };
        feed.Children.Add(_feedFolder);
        feed.Children.Add(_chooseFeed);
        bottom.Children.Add(feed);
        DockPanel.SetDock(bottom, Dock.Bottom);
        left.Children.Add(bottom);
        left.Children.Add(_changelog);

        var right = new DockPanel { Margin = new Thickness(10) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(_recheck);
        buttons.Children.Add(_build);
        buttons.Children.Add(new TextBlock { Text = "Passwort:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        buttons.Children.Add(_password);
        buttons.Children.Add(_sign);
        buttons.Children.Add(_publish);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_retry);
        DockPanel.SetDock(_steps, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(_steps);
        right.Children.Add(buttons);
        right.Children.Add(_log);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Prüft Version und Changelog sofort; setzt die Fehlerzeilen und die Knöpfe.</summary>
    private bool Validate()
    {
        var versionProblem = ReleaseVersion.Problem(_version.Text, _current, IsLocal, out var version);
        _versionError.Text = versionProblem ?? string.Empty;
        var changelogProblems = IsLocal || versionProblem is not null || !File.Exists(_paths.Changelog)
            ? []
            : Changelog.Problems(_changelog.Text, File.ReadAllText(_paths.Changelog), version);
        _changelogError.Text = string.Join(Environment.NewLine, changelogProblems);
        var valid = versionProblem is null && changelogProblems.Count == 0;
        UpdateButtons(valid);
        return valid;
    }

    private void UpdateButtons(bool? valid = null)
    {
        var inputsOk = valid ?? (ReleaseVersion.Problem(_version.Text, _current, IsLocal, out _) is null && _changelogError.Text.Length == 0);
        var editable = !_busy && !_built;
        _version.IsEnabled = _changelog.IsEnabled = _localFeed.IsEnabled = _feedFolder.IsEnabled = _chooseFeed.IsEnabled = editable;
        // Vorprüfungen sperren den Knopf nicht: "Bauen und testen" prüft selbst noch einmal mit den aktuellen Eingaben (sonst bliebe er nach
        // einem außerhalb behobenen Problem gesperrt).
        _build.IsEnabled = editable && inputsOk;
        _recheck.IsEnabled = editable;
        _password.IsEnabled = _sign.IsEnabled = !_busy && _built && !_signed;
        _publish.IsEnabled = !_busy && _signed && !IsLocal && !_finished;
        _cancel.IsEnabled = _busy;
        _retry.Visibility = _states[ReleaseStep.GitHubRelease] == StepState.Failed && !_busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private ReleaseRun NewRun()
    {
        ReleaseVersion.Problem(_version.Text, _current, IsLocal, out var version);
        var run = new ReleaseRun(
            new ReleaseOptions(_paths, version, _changelog.Text, IsLocal ? _feedFolder.Text.Trim() : null, DateOnly.FromDateTime(DateTime.Now), ReleasePaths.KeyFile, _gh),
            new ProcessCommandRunner());
        run.StepChanged += (step, state) => Dispatcher.BeginInvoke(() =>
        {
            _states[step] = state;
            ShowSteps();
        });
        run.Log += line => Dispatcher.BeginInvoke(() =>
        {
            _log.AppendText(line + Environment.NewLine);
            _log.ScrollToEnd();
        });
        return run;
    }

    private async Task CheckAsync()
    {
        if (_busy || _built)
        {
            return;
        }

        Validate();
        ShowSteps();

        // Den Lauf auf dem UI-Thread anlegen: er liest die Eingabefelder.
        var run = NewRun();
        await Busy(async token =>
        {
            await run.CheckAsync(token);
            return true;
        });
    }

    private async Task BuildAsync()
    {
        if (!Validate())
        {
            return;
        }

        foreach (var step in _states.Keys.ToList())
        {
            _states[step] = StepState.Waiting;
        }

        _run = NewRun();
        var run = _run;
        await Busy(async token =>
        {
            var problems = await run.CheckAsync(token);
            _built = problems.Count == 0 && await run.BuildAsync(token);
            return _built;
        });
    }

    private void Sign()
    {
        if (_run is null)
        {
            return;
        }

        SignResult result;
        try
        {
            result = _run.Sign(_password.Password);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.AppendText("FEHLER: " + ex.Message + Environment.NewLine);
            result = SignResult.VerifyFailed;
        }

        _password.Clear();
        _signed = result == SignResult.Ok;
        _finished = _signed && IsLocal;
        if (_finished)
        {
            MessageBox.Show(this, "Lokaler Feed fertig.", Title);
        }

        UpdateButtons();
    }

    private async Task PublishAsync()
    {
        if (_run is not { } run)
        {
            return;
        }

        var summary = $"Release v{_version.Text.Trim()} nach {ReleaseRun.Repository} mit:{Environment.NewLine}"
            + string.Join(Environment.NewLine, run.Files.Select(f => $"  {Path.GetFileName(f.Path)} ({f.Bytes / 1024.0 / 1024.0:0.0} MB)"))
            + $"{Environment.NewLine}{Environment.NewLine}Öffentlicher Commit 'Release v{_version.Text.Trim()}' als {PublicCommit.Name} <{PublicCommit.Email}>, ohne {string.Join(", ", PublicCommit.Excluded)}."
            + $"{Environment.NewLine}{Environment.NewLine}Notizen:{Environment.NewLine}{run.Notes}{Environment.NewLine}Commit, Tag, Push und Veröffentlichung jetzt ausführen?";
        if (MessageBox.Show(this, summary, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            run.Revert();
            _run = null;
            _built = _signed = false;
            UpdateButtons();
            return;
        }

        await Busy(async token =>
        {
            _finished = await run.PublishAsync(token);
            return _finished;
        });
    }

    private async Task RetryAsync()
    {
        if (_run is { } run)
        {
            await Busy(async token =>
            {
                _finished = await run.RetryReleasePageAsync(token);
                return _finished;
            });
        }
    }

    /// <summary>Führt einen Schritt aus, sperrt dabei die Knöpfe; schließt danach das Fenster, falls das Schließen darauf wartet.</summary>
    private async Task Busy(Func<CancellationToken, Task<bool>> work)
    {
        _busy = true;
        _cancellation = new CancellationTokenSource();
        UpdateButtons();
        try
        {
            await Task.Run(() => work(_cancellation.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log.AppendText("FEHLER: " + ex.Message + Environment.NewLine);
        }
        finally
        {
            _busy = false;
            _cancellation.Dispose();
            _cancellation = null;
            if (!_built)
            {
                _signed = false;
            }

            UpdateButtons();
            if (_closeWhenIdle)
            {
                Close();
            }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            if (MessageBox.Show(this, "Ein Schritt läuft noch. Abbrechen und schließen?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _closeWhenIdle = true;
                _cancellation?.Cancel();
            }

            return;
        }

        _run?.Revert();
    }

    /// <summary>Bei einem unerwarteten Absturz csproj und Changelog zurücknehmen, solange nicht committet (Spec "Fehler und Zurücknehmen").</summary>
    public void RevertAfterCrash() => _run?.Revert();

    private void ChooseFeed()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(_feedFolder.Text) ? _feedFolder.Text : null };
        if (dialog.ShowDialog(this) == true)
        {
            _feedFolder.Text = dialog.FolderName;
        }
    }

    private void ShowSteps()
    {
        var local = IsLocal;
        _steps.ItemsSource = _states
            .Where(pair => local ? pair.Key <= ReleaseStep.LocalFeed : pair.Key != ReleaseStep.LocalFeed)
            .Select(pair => $"{Symbol(pair.Value)}  {StepNames[pair.Key]}")
            .ToList();
    }

    private static string Symbol(StepState state) => state switch
    {
        StepState.Running => "▶",
        StepState.Ok => "✓",
        StepState.Failed => "✗",
        StepState.Skipped => "–",
        _ => "·",
    };

    private static TextBlock Error() => new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    private static Button NewButton(string text) => new() { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4) };
}
