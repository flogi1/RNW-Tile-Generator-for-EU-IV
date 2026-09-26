using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RnwTileGenerator.App;

/// <summary>Application icon shared by the main window, dialogs and the splash screen.
/// app.ico is compiled in as a WPF resource (see the .csproj), so it also works when the
/// program is published as a single file.</summary>
public static class AppIcon
{
    private static BitmapFrame? _frame;

    public static BitmapFrame? Frame
    {
        get
        {
            if (_frame != null) return _frame;
            try
            {
                var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/RnwTileGenerator;component/app.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                // Largest frame; WPF scales it down for window chrome and taskbar.
                BitmapFrame best = decoder.Frames[0];
                foreach (var f in decoder.Frames)
                    if (f.PixelWidth > best.PixelWidth) best = f;
                best.Freeze();
                _frame = best;
            }
            catch
            {
                _frame = null; // a missing icon must never stop the program from starting
            }
            return _frame;
        }
    }
}

/// <summary>Small start-up window with a progress bar. The UI thread is busy while the
/// program initialises, so every <see cref="Step"/> pumps the dispatcher once to make the
/// bar and text repaint before the next (blocking) start-up step runs.</summary>
public sealed class SplashWindow : Window
{
    private readonly ProgressBar _bar;
    private readonly TextBlock _status;

    public SplashWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 420;
        Height = 230;
        Background = new SolidColorBrush(Color.FromRgb(20, 42, 68));
        Icon = AppIcon.Frame;

        var root = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(90, 190, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24),
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (AppIcon.Frame != null)
        {
            stack.Children.Add(new Image
            {
                Source = AppIcon.Frame,
                Width = 72,
                Height = 72,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }
        stack.Children.Add(new TextBlock
        {
            Text = "RNW Tile Generator",
            Foreground = Brushes.White,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 14),
        });
        _bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, Foreground = new SolidColorBrush(Color.FromRgb(90, 190, 255)) };
        stack.Children.Add(_bar);
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(190, 205, 220)),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(_status);

        root.Child = stack;
        Content = root;
    }

    /// <summary>Sets the bar (0..1) and status text and forces one repaint.</summary>
    public void Step(double fraction, string text)
    {
        _bar.Value = Math.Max(0, Math.Min(1, fraction));
        _status.Text = text;
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }
}
