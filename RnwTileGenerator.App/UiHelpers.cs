using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RnwTileGenerator.App;

/// <summary>
/// Small factory helpers for building the code-only WPF UI compactly -
/// this project has no .xaml files, so every control tree is built by
/// hand in C#; these keep that from being unbearably verbose.
/// </summary>
public static class Ui
{
    public static TextBlock Bold(string text, double size = 13) => new()
    {
        Text = text,
        FontWeight = FontWeights.Bold,
        FontSize = size,
        Margin = new Thickness(0, 0, 0, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    public static TextBlock Wrap(string text, double width = 230) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Width = width,
        Margin = new Thickness(0, 0, 0, 8),
    };

    /// <summary>An info/status label whose Text can be updated later.</summary>
    public static TextBlock Info(double width = 230) => new()
    {
        Text = "",
        TextWrapping = TextWrapping.Wrap,
        Width = width,
        Margin = new Thickness(0, 10, 0, 0),
    };

    public static Button Button(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6, 3, 6, 3) };
        b.Click += onClick;
        return b;
    }

    public static CheckBox CheckBoxCtl(string text, bool isChecked, RoutedEventHandler onChanged)
    {
        var cb = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 0) };
        cb.Checked += onChanged;
        cb.Unchecked += onChanged;
        return cb;
    }

    public static RadioButton Radio(string text, string groupName, bool isChecked, RoutedEventHandler onChecked)
    {
        var r = new RadioButton { Content = text, GroupName = groupName, IsChecked = isChecked, Margin = new Thickness(0, 3, 0, 0) };
        r.Checked += onChecked;
        return r;
    }

    public static GroupBox Group(string header, UIElement content) => new()
    {
        Header = header,
        Content = content,
        Margin = new Thickness(0, 0, 0, 10),
        Padding = new Thickness(6),
    };

    public static StackPanel Stack(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static StackPanel Horizontal(params UIElement[] children)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    public static Separator Sep() => new() { Margin = new Thickness(0, 8, 0, 8) };

    /// <summary>Label + slider stacked vertically, plus a directly-editable
    /// value textbox kept in sync both ways (Runde 9 feedback: "Füge bei
    /// allen Slidern, die es noch nicht haben auch eine Box hinzu, wo der
    /// Wert direkt eingegeben werden kann."). The returned Slider's Value is
    /// read directly by the caller wherever the Python original read a
    /// tk.DoubleVar/IntVar. The label always shows the slider's current
    /// numeric value appended after the base text (e.g. "Water Percentage:
    /// 55"), kept live via an internally-chained ValueChanged handler -
    /// callers keep passing their own onChanged unchanged, and every
    /// existing call site picks this up automatically (Runde 7, dritte
    /// Rückmeldung: "bei allen Slidern auch ein Zahlenwert"; Runde 9 adds
    /// the editable box on top of that same label). Formatted with "0.##"
    /// (not integer rounding) so fractional 0-1-range sliders such as
    /// MountainAmount/CoastDetail/LakeFrequency display and accept decimals
    /// correctly, same as wide integer-range sliders like WaterPercent.</summary>
    public static Slider LabeledSlider(StackPanel into, string label, double min, double max, double value, RoutedPropertyChangedEventHandler<double>? onChanged = null, string? tooltip = null)
    {
        var labelBlock = new TextBlock { Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        string FmtLabel(double v) => $"{label}: {v.ToString("0.##")}";
        string FmtBox(double v) => v.ToString("0.##");
        labelBlock.Text = FmtLabel(value);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = false };
        var box = new TextBox { Width = 70, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        box.Text = FmtBox(value);
        if (tooltip != null) { labelBlock.ToolTip = tooltip; slider.ToolTip = tooltip; box.ToolTip = tooltip; }

        bool syncing = false;
        slider.ValueChanged += (s, e) =>
        {
            labelBlock.Text = FmtLabel(e.NewValue);
            if (!syncing)
            {
                syncing = true;
                box.Text = FmtBox(e.NewValue);
                syncing = false;
            }
            onChanged?.Invoke(s, e);
        };
        box.LostFocus += (_, _) =>
        {
            if (syncing || !double.TryParse(box.Text, out var v)) return;
            v = Math.Clamp(v, min, max);
            syncing = true;
            slider.Value = v;
            box.Text = FmtBox(v);
            syncing = false;
        };

        into.Children.Add(labelBlock);
        into.Children.Add(slider);
        into.Children.Add(box);
        return slider;
    }

    /// <summary>Label + slider + an editable exact-value textbox, kept in
    /// sync both ways - for ranges wide enough (e.g. 0-10000) that hitting
    /// an exact number by dragging the slider alone is impractical, and
    /// anywhere the current number itself needs to be visibly on screen
    /// rather than only implied by the slider handle's position. Returns
    /// the Slider; its Value is authoritative, same contract as
    /// LabeledSlider.</summary>
    public static Slider LabeledSliderWithValue(StackPanel into, string label, double min, double max, double value, Action<double>? onChanged = null, string? tooltip = null, int decimals = 0)
    {
        var labelBlock = new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = false };
        var box = new TextBox { Width = 70, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 6) };
        string Fmt(double v) => decimals > 0 ? v.ToString("0." + new string('0', decimals)) : ((int)Math.Round(v)).ToString();
        box.Text = Fmt(value);
        if (tooltip != null) { labelBlock.ToolTip = tooltip; slider.ToolTip = tooltip; box.ToolTip = tooltip; }

        bool syncing = false;
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true;
            box.Text = Fmt(e.NewValue);
            syncing = false;
            onChanged?.Invoke(e.NewValue);
        };
        box.LostFocus += (_, _) =>
        {
            if (syncing || !double.TryParse(box.Text, out var v)) return;
            v = Math.Clamp(v, min, max);
            syncing = true;
            slider.Value = v;
            box.Text = Fmt(v);
            syncing = false;
            onChanged?.Invoke(v);
        };

        into.Children.Add(labelBlock);
        into.Children.Add(slider);
        into.Children.Add(box);
        return slider;
    }

    /// <summary>Fixed-width scrollable sidebar column, matching the
    /// Python side panels' pack_propagate(False) fixed-width behavior.</summary>
    public static ScrollViewer Sidebar(double width, UIElement content)
    {
        var host = new Border
        {
            Width = width,
            Padding = new Thickness(8),
            Child = content,
        };
        return new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    /// <summary>Sidebar (fixed width, left) + canvas (fills remaining
    /// space, right), matching every stage's `side.pack(LEFT) ; canvas.pack(LEFT, fill=BOTH, expand=True)` layout.</summary>
    public static Grid SideAndCanvas(UIElement sidebar, UIElement canvas)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(sidebar, 0);
        Grid.SetColumn(canvas, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(canvas);
        return grid;
    }
}
