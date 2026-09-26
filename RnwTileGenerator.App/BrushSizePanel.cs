using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RnwTileGenerator.App;

/// <summary>
/// Reusable brush-size control: a slider (as before) plus a synced numeric
/// box showing/accepting the exact value, a "single pixel" override, and 4
/// presets the user can apply (left-click) or overwrite with the current
/// size (right-click) - persisted via BrushPresets. Used everywhere a
/// stage previously built its own ad-hoc "Ui.LabeledSlider(...)" for a
/// brush/pen size (Coastline, Mountains, Provinces manual pen), so all
/// three now share one consistent, more precisely controllable widget.
/// </summary>
public sealed class BrushSizePanel
{
    public UIElement View { get; }
    public double Value { get; private set; }
    public bool SinglePixel { get; private set; }

    /// <summary>What a stage should actually paint with: 0 (an exact
    /// single pixel) when the override is on, otherwise the slider value.</summary>
    public double EffectiveRadius => SinglePixel ? 0 : Value;

    /// <summary>Fires whenever Value, SinglePixel, or a preset changes -
    /// stages use this to keep the canvas's hover-radius preview in sync.</summary>
    public event Action? Changed;

    private readonly Slider _slider;
    private readonly TextBox _box;
    private readonly CheckBox _singlePixelBox;
    private readonly string _presetKey;
    private readonly double[] _presetDefaults;
    private readonly Button[] _presetButtons = new Button[4];

    public BrushSizePanel(string label, double min, double max, double initial, string presetKey, double[] presetDefaults, string? tooltip = null)
    {
        _presetKey = presetKey;
        _presetDefaults = presetDefaults;

        var root = Ui.Stack();
        var labelBlock = new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        if (tooltip != null) labelBlock.ToolTip = tooltip;
        root.Children.Add(labelBlock);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        // _box built before _slider's ValueChanged handler below (which
        // references it) so the compiler's nullable flow analysis sees the
        // readonly field as definitely assigned at the point that lambda
        // captures it - same reasoning as StageCoastline.cs's _canvas.
        _box = new TextBox { Width = 55, Margin = new Thickness(6, 0, 0, 0), Text = FormatValue(initial), VerticalContentAlignment = VerticalAlignment.Center };
        _box.ToolTip = Loc.T("brush.exactSize.tip");
        _slider = new Slider { Minimum = min, Maximum = max, Value = initial, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        if (tooltip != null) _slider.ToolTip = tooltip;
        _slider.ValueChanged += (_, e) =>
        {
            Value = e.NewValue;
            _box.Text = FormatValue(Value);
            Changed?.Invoke();
        };
        _box.LostFocus += (_, _) => ApplyBoxText();
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyBoxText(); Keyboard.ClearFocus(); e.Handled = true; } };
        row.Children.Add(_slider);
        row.Children.Add(_box);
        row.Children.Add(new TextBlock { Text = "px", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(row);

        _singlePixelBox = new CheckBox
        {
            Content = Loc.T("brush.singlePixel"),
            Margin = new Thickness(0, 3, 0, 0),
            ToolTip = Loc.T("brush.singlePixel.tip"),
        };
        _singlePixelBox.Checked += (_, _) => { SinglePixel = true; UpdateSliderEnabled(); Changed?.Invoke(); };
        _singlePixelBox.Unchecked += (_, _) => { SinglePixel = false; UpdateSliderEnabled(); Changed?.Invoke(); };
        root.Children.Add(_singlePixelBox);

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        var stored = BrushPresets.Get(presetKey, presetDefaults);
        for (int i = 0; i < 4; i++)
        {
            int idx = i;
            var b = new Button
            {
                Content = FormatValue(stored[i]),
                Width = 42,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0, 2, 0, 2),
                ToolTip = Loc.T("brush.preset.tip"),
            };
            b.Click += (_, _) =>
            {
                var current = BrushPresets.Get(_presetKey, _presetDefaults);
                SetValue(current[idx]);
            };
            b.MouseRightButtonUp += (_, e) =>
            {
                BrushPresets.SetSlot(_presetKey, idx, Value, _presetDefaults);
                b.Content = FormatValue(Value);
                e.Handled = true;
            };
            _presetButtons[i] = b;
            presetRow.Children.Add(b);
        }
        root.Children.Add(presetRow);

        Value = initial;
        View = root;
    }

    public void SetValue(double v)
    {
        v = Math.Clamp(v, _slider.Minimum, _slider.Maximum);
        _slider.Value = v; // triggers ValueChanged -> updates Value/box and fires Changed
    }

    private void UpdateSliderEnabled()
    {
        // Kept enabled (not IsEnabled=false) so the user can still queue up
        // a new size while single-pixel is active - just visually muted.
        _slider.Opacity = SinglePixel ? 0.5 : 1.0;
        _box.Opacity = SinglePixel ? 0.5 : 1.0;
    }

    private void ApplyBoxText()
    {
        if (double.TryParse(_box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
            double.TryParse(_box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
        {
            SetValue(v);
        }
        else
        {
            _box.Text = FormatValue(Value);
        }
    }

    private static string FormatValue(double v) =>
        Math.Abs(v - Math.Round(v)) < 0.001 ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture);
}
