using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace RnwTileGenerator.App;

/// <summary>
/// Small progress window with a status line, a progress bar and a Cancel button (download of an update,
/// update mode). Closing it with the window's X counts as Cancel while the work is still running; the
/// caller closes it with <see cref="CloseAfterFinished"/>.
/// </summary>
public sealed class ProgressWindow : Window
{
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private readonly Action _onCancel;
    private bool _finished;

    public ProgressWindow(string title, string hint, string cancelText, Action onCancel)
    {
        _onCancel = onCancel;
        Title = title;
        Icon = AppIcon.Frame;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        root.Children.Add(_status);
        _bar = new ProgressBar { Height = 18, IsIndeterminate = true };
        root.Children.Add(_bar);
        _cancel = new Button { Content = cancelText, Width = 110, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        _cancel.Click += (_, _) => Cancel();
        root.Children.Add(_cancel);
        Content = root;
    }

    /// <summary>Shows <paramref name="status"/>; <paramref name="total"/> 0 means "unknown" (moving bar).</summary>
    public void Report(string status, long done, long total)
    {
        _status.Text = status;
        if (total > 0)
        {
            _bar.IsIndeterminate = false;
            _bar.Maximum = total;
            _bar.Value = Math.Clamp(done, 0, total);
        }
        else
        {
            _bar.IsIndeterminate = true;
        }
    }

    public void CloseAfterFinished()
    {
        _finished = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            Cancel();
        }

        base.OnClosing(e);
    }

    private void Cancel()
    {
        _cancel.IsEnabled = false;
        _onCancel();
    }
}
