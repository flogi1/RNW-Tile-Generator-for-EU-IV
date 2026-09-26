using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace RnwTileGenerator.App;

/// <summary>
/// Small modal helper dialogs, ported from the little tk.Toplevel helpers
/// scattered across the original Python GUI (NewTileDialog in gui/app.py,
/// and the three _*Dialog classes at the bottom of gui/stage_metadata.py).
/// All built as plain code (no .xaml), matching the rest of this project.
/// </summary>
public static class Dialogs
{
    private static Window BaseDialog(string title, Window owner, double width = 380)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height,
            Width = width,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        return w;
    }

    private static StackPanel ButtonRow(Window w, Action onOk)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 12) };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(4, 0, 4, 0) };
        ok.Click += (_, _) => { onOk(); w.Close(); };
        var cancel = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(4, 0, 4, 0) };
        cancel.Click += (_, _) => w.Close();
        row.Children.Add(ok);
        row.Children.Add(cancel);
        return row;
    }

    /// <summary>Pick from three dropdowns (used for "add strait": from/to/through).</summary>
    public static (string a, string b, string c)? ThreePick(Window owner, string title, string labelA, string labelB, string labelC, IReadOnlyList<string> choices)
    {
        var w = BaseDialog(title, owner, 440);
        (string a, string b, string c)? result = null;
        var body = new StackPanel { Margin = new Thickness(10) };

        var combos = new List<ComboBox>();
        foreach (var label in new[] { labelA, labelB, labelC })
        {
            body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            var cb = new ComboBox { ItemsSource = choices, IsEditable = false };
            body.Children.Add(cb);
            combos.Add(cb);
        }

        var row = ButtonRow(w, () =>
        {
            if (combos[0].SelectedItem is string a && combos[1].SelectedItem is string b && combos[2].SelectedItem is string c)
                result = (a, b, c);
        });
        body.Children.Add(row);
        w.Content = body;
        w.ShowDialog();
        return result;
    }

    /// <summary>Pick from a dropdown, plus a free-text field (used for
    /// "add province name").</summary>
    public static (string pick, string text)? PickAndText(Window owner, string title, string pickLabel, IReadOnlyList<string> choices, string textLabel)
    {
        var w = BaseDialog(title, owner);
        (string pick, string text)? result = null;
        var body = new StackPanel { Margin = new Thickness(10) };

        body.Children.Add(new TextBlock { Text = pickLabel, Margin = new Thickness(0, 6, 0, 2) });
        var pickBox = new ComboBox { ItemsSource = choices, IsEditable = false };
        body.Children.Add(pickBox);

        body.Children.Add(new TextBlock { Text = textLabel, Margin = new Thickness(0, 10, 0, 2) });
        var textBox = new TextBox();
        body.Children.Add(textBox);

        var row = ButtonRow(w, () =>
        {
            if (pickBox.SelectedItem is string pick && !string.IsNullOrEmpty(pick))
                result = (pick, textBox.Text);
        });
        body.Children.Add(row);
        w.Content = body;
        w.ShowDialog();
        return result;
    }

    /// <summary>Pick from a dropdown, plus an editable combo (used for
    /// "add modifier": the modifier name is free text with common
    /// suggestions).</summary>
    public static (string pick, string combo)? PickAndCombo(Window owner, string title, string pickLabel, IReadOnlyList<string> choices, string comboLabel, IReadOnlyList<string> comboValues)
    {
        var w = BaseDialog(title, owner);
        (string pick, string combo)? result = null;
        var body = new StackPanel { Margin = new Thickness(10) };

        body.Children.Add(new TextBlock { Text = pickLabel, Margin = new Thickness(0, 6, 0, 2) });
        var pickBox = new ComboBox { ItemsSource = choices, IsEditable = false };
        body.Children.Add(pickBox);

        body.Children.Add(new TextBlock { Text = comboLabel, Margin = new Thickness(0, 10, 0, 2) });
        var comboBox = new ComboBox { ItemsSource = comboValues, IsEditable = true };
        body.Children.Add(comboBox);

        var row = ButtonRow(w, () =>
        {
            if (pickBox.SelectedItem is string pick && !string.IsNullOrEmpty(pick))
                result = (pick, comboBox.Text ?? "");
        });
        body.Children.Add(row);
        w.Content = body;
        w.ShowDialog();
        return result;
    }

    /// <summary>Two free-text prompts in one dialog (used for "add extra
    /// field": field name + value, matching two chained
    /// simpledialog.askstring calls in the original).</summary>
    public static (string key, string value)? TwoText(Window owner, string title, string labelA, string labelB)
    {
        var w = BaseDialog(title, owner, 340);
        (string key, string value)? result = null;
        var body = new StackPanel { Margin = new Thickness(10) };

        body.Children.Add(new TextBlock { Text = labelA, Margin = new Thickness(0, 6, 0, 2) });
        var keyBox = new TextBox();
        body.Children.Add(keyBox);

        body.Children.Add(new TextBlock { Text = labelB, Margin = new Thickness(0, 10, 0, 2) });
        var valBox = new TextBox();
        body.Children.Add(valBox);

        var row = ButtonRow(w, () =>
        {
            if (!string.IsNullOrWhiteSpace(keyBox.Text))
                result = (keyBox.Text.Trim(), valBox.Text);
        });
        body.Children.Add(row);
        w.Content = body;
        w.ShowDialog();
        return result;
    }

    /// <summary>A single editable-combo prompt (used for "pick/type a
    /// modifier name" after a province has already been chosen by clicking
    /// the map, so - unlike PickAndCombo - there is no separate province
    /// dropdown here).</summary>
    public static string? ComboPick(Window owner, string title, string label, IReadOnlyList<string> choices, string initial = "")
    {
        var w = BaseDialog(title, owner, 340);
        string? result = null;
        var body = new StackPanel { Margin = new Thickness(10) };

        body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        var combo = new ComboBox { ItemsSource = choices, IsEditable = true, Text = initial };
        body.Children.Add(combo);

        var row = ButtonRow(w, () => result = (combo.Text ?? "").Trim());
        body.Children.Add(row);
        w.Content = body;
        w.ShowDialog();
        return result;
    }

    /// <summary>A single free-text prompt (used for "note for this saved seed").</summary>
    public static string? OneText(Window owner, string title, string label, string initial = "")
    {
        var w = BaseDialog(title, owner, 340);
        string? result = null;
        var body = new StackPanel { Margin = new Thickness(10) };

        body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        var box = new TextBox { Text = initial };
        body.Children.Add(box);

        var row = ButtonRow(w, () => result = box.Text);
        body.Children.Add(row);
        w.Content = body;
        w.ShowDialog();
        return result;
    }

    public static bool Confirm(Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void Info(Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Warn(Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Error(Window owner, string title, string message) =>
        MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
