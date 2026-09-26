using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace RnwTileGenerator.App;

/// <summary>
/// Global "never crash silently" safety net. Before this existed, any
/// unhandled exception (e.g. thrown from inside a Dispatcher.BeginInvoke
/// callback, which is how PaintCanvasControl schedules redraws) just
/// terminated the process with nothing visible - no console window, no
/// dialog. This wires up every place .NET/WPF can report an unhandled
/// exception, writes the full exception (with inner exceptions and stack
/// trace) to a log file next to the executable, and shows it in a plain
/// scrollable window so it can be read and copied. For exceptions raised
/// on the UI dispatcher (by far the most common kind here, e.g. a redraw
/// or a click handler misbehaving) the app is kept alive afterwards
/// instead of exiting, since those are almost always recoverable.
/// </summary>
public static class CrashHandling
{
    internal const string LogFileName = "crash_log.txt";

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, LogFileName);

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log(e.Exception, "DispatcherUnhandledException (UI thread)");
            Show(e.Exception, canContinue: true);
            e.Handled = true; // keep the app running - almost always recoverable
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log(ex, "AppDomain.UnhandledException (non-UI thread, process will exit)");
                // Not on the UI thread and the process is terminating regardless -
                // still try to surface it, best-effort.
                try { app.Dispatcher.Invoke(() => Show(ex, canContinue: false)); } catch { /* best effort */ }
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };
    }

    private static void Log(Exception ex, string source)
    {
        try
        {
            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{Format(ex)}\n\n";
            File.AppendAllText(LogPath, text, Encoding.UTF8);
        }
        catch { /* logging must never itself throw */ }
    }

    private static string Format(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        int depth = 0;
        while (current != null)
        {
            sb.Append(depth == 0 ? "" : "\n--- inner exception ---\n");
            sb.Append(current.GetType().FullName).Append(": ").Append(current.Message).Append('\n');
            sb.Append(current.StackTrace);
            sb.Append('\n');
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    private static void Show(Exception ex, bool canContinue)
    {
        try
        {
            var win = new Window
            {
                Title = Loc.T(canContinue ? "crash.title.continue" : "crash.title.fatal"),
                Width = 760,
                Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            };

            var root = new DockPanel { Margin = new Thickness(10) };

            var header = new TextBlock
            {
                Text = Loc.T(canContinue ? "crash.header.continue" : "crash.header.fatal"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            header.SetValue(DockPanel.DockProperty, Dock.Top);
            root.Children.Add(header);

            var text = Format(ex);
            var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttonRow.SetValue(DockPanel.DockProperty, Dock.Bottom);
            var reportBtn = new Button { Content = Loc.T("crash.report"), MinWidth = 120, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 3, 6, 3) };
            reportBtn.Click += (_, _) => new BugReportWindow(win, text).ShowDialog();
            var copyBtn = new Button { Content = Loc.T("crash.copy"), MinWidth = 190, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 3, 6, 3) };
            copyBtn.Click += (_, _) => { try { Clipboard.SetText(text); } catch { /* ignore */ } };
            var closeBtn = new Button { Content = Loc.T(canContinue ? "crash.close" : "crash.quit"), MinWidth = 150, Padding = new Thickness(6, 3, 6, 3) };
            closeBtn.Click += (_, _) => win.Close();
            buttonRow.Children.Add(reportBtn);
            buttonRow.Children.Add(copyBtn);
            buttonRow.Children.Add(closeBtn);
            root.Children.Add(buttonRow);

            var box = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
            };
            root.Children.Add(box);

            win.Content = root;
            win.ShowDialog();
        }
        catch
        {
            // If even the crash dialog fails, fall back to the plainest
            // possible notification rather than throwing again.
            try { MessageBox.Show(ex.ToString(), "Fehler"); } catch { /* truly nothing left to try */ }
        }
    }
}
