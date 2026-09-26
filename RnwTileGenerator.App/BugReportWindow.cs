using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// Help -> Report a bug (and "Report bug" in the crash dialog). The user fills in a title and a few free texts;
/// the window shows the complete English report and sends nothing by itself: "Report on GitHub" opens a prefilled
/// issue in the browser, "Send by email" the mail program (mailto), "Copy report" puts the text into the clipboard.
/// Paths in the crash log are anonymized (see BugReport.Anonymize).
/// </summary>
public sealed class BugReportWindow : Window
{
    private static string CrashLogPath => Path.Combine(AppContext.BaseDirectory, CrashHandling.LogFileName);

    private readonly TextBox _title = new();
    private readonly TextBox _what = MultiLine();
    private readonly TextBox _steps = MultiLine("1. \n2. ");
    private readonly TextBox _expected = MultiLine();
    private readonly CheckBox _includeLog = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _preview = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 11,
    };
    private readonly Button _github, _email, _copy;
    private readonly string? _crashLog;

    public BugReportWindow(Window? owner, string? prefilledError = null)
    {
        Title = Loc.T("bug.title");
        Icon = AppIcon.Frame;
        Width = 900;
        Height = 640;
        MinWidth = 640;
        MinHeight = 480;
        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _crashLog = ReadCrashLog();
        if (prefilledError != null)
            _what.Text = BugReport.Anonymize(prefilledError, UserProfile);

        var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        left.Children.Add(new TextBlock { Text = Loc.T("bug.intro"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        AddField(left, "bug.field.title", _title);
        AddField(left, "bug.field.what", _what);
        AddField(left, "bug.field.steps", _steps);
        AddField(left, "bug.field.expected", _expected);
        _includeLog.Content = Loc.T("bug.includeLog");
        _includeLog.IsChecked = _crashLog != null;
        _includeLog.Visibility = _crashLog != null ? Visibility.Visible : Visibility.Collapsed;
        left.Children.Add(_includeLog);
        left.Children.Add(new TextBlock { Text = Loc.T("bug.attachHint"), TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 10, 0, 0) });

        var right = new DockPanel();
        var previewLabel = new TextBlock { Text = Loc.T("bug.preview"), Margin = new Thickness(0, 0, 0, 4) };
        previewLabel.SetValue(DockPanel.DockProperty, Dock.Top);
        right.Children.Add(previewLabel);
        right.Children.Add(_preview);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftScroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(right, 1);
        columns.Children.Add(leftScroll);
        columns.Children.Add(right);

        _github = MakeButton(Loc.T("bug.github"), Loc.T("bug.github.tip"), (_, _) => OpenLink(BugReport.GitHubUrl(AppInfo.Repository, Input()), isMail: false));
        _email = MakeButton(Loc.T("bug.email"), Loc.T("bug.email.tip"), (_, _) => OpenLink(BugReport.MailtoUrl(AppInfo.BugReportEmail, Input()), isMail: true));
        _copy = MakeButton(Loc.T("bug.copy"), null, (_, _) => { if (CopyReport()) Dialogs.Info(this, Title, Loc.T("bug.copied")); });
        var folder = MakeButton(Loc.T("bug.openFolder"), Loc.T("bug.openFolder.tip"), (_, _) => Shell(AppContext.BaseDirectory));
        var close = MakeButton(Loc.T("bug.close"), null, (_, _) => Close());
        close.IsCancel = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var b in new[] { _github, _email, _copy, folder, close }) buttons.Children.Add(b);

        var root = new DockPanel { Margin = new Thickness(12) };
        buttons.SetValue(DockPanel.DockProperty, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(columns);
        Content = root;

        foreach (var box in new[] { _title, _what, _steps, _expected }) box.TextChanged += (_, _) => Refresh();
        _includeLog.Click += (_, _) => Refresh();
        Refresh();
        Loaded += (_, _) => _title.Focus();
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static TextBox MultiLine(string text = "") => new()
    {
        Text = text,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 70,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };

    private static void AddField(Panel panel, string labelKey, Control box)
    {
        panel.Children.Add(new TextBlock { Text = Loc.T(labelKey), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
        panel.Children.Add(box);
    }

    private static Button MakeButton(string text, string? tip, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, ToolTip = tip, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        ToolTipService.SetShowOnDisabled(button, true);
        button.Click += onClick;
        return button;
    }

    private static string? ReadCrashLog()
    {
        try
        {
            if (!File.Exists(CrashLogPath)) return null;
            var tail = BugReport.LastCrashEntries(File.ReadAllText(CrashLogPath), BugReport.MaxCrashLogChars);
            return string.IsNullOrWhiteSpace(tail) ? null : BugReport.Anonymize(tail, UserProfile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private BugReportInput Input() => new(
        _title.Text.Trim(),
        _what.Text,
        _steps.Text,
        _expected.Text,
        _includeLog.IsChecked == true ? _crashLog : null,
        AppInfo.Version.ToString(),
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        Loc.DisplayName(Loc.Current));

    private void Refresh()
    {
        _preview.Text = BugReport.BuildText(Input());
        var hasTitle = _title.Text.Trim().Length > 0;
        foreach (var b in new[] { _github, _email, _copy })
        {
            b.IsEnabled = hasTitle;
        }

        _github.ToolTip = hasTitle ? Loc.T("bug.github.tip") : Loc.T("bug.titleRequired");
        _email.ToolTip = hasTitle ? Loc.T("bug.email.tip") : Loc.T("bug.titleRequired");
    }

    private void OpenLink(BugReportLink link, bool isMail)
    {
        if (link.Truncated) CopyReport();
        try
        {
            Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (isMail)
            {
                CopyReport();
                Dialogs.Warn(this, Title, string.Format(Loc.T("bug.noMailProgram"), AppInfo.BugReportEmail));
            }
            else
            {
                Dialogs.Error(this, UpdateTexts.ErrorTitle, UpdateTexts.BrowserFailed(ex.Message));
            }

            return;
        }

        if (link.Truncated) Dialogs.Info(this, Title, Loc.T("bug.truncated"));
    }

    /// <summary>Whole report incl. title as plain text into the clipboard. The clipboard can be locked by another program.</summary>
    private bool CopyReport()
    {
        try
        {
            Clipboard.SetText(_title.Text.Trim() + "\r\n\r\n" + BugReport.BuildText(Input()));
            return true;
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            return false;
        }
    }

    private void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Dialogs.Error(this, UpdateTexts.ErrorTitle, ex.Message); }
    }
}
