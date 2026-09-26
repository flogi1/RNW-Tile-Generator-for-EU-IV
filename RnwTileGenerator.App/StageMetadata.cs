using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Metadata stage: the tile-wide, non-per-province parts of the
/// .txt file - rotation/placement flags, weight, empty color, and passthrough
/// extra fields. Straits/regions/province names/modifiers now live in the
/// separate Special Features tab (shown before this one) - see
/// StageSpecialFeatures. Port of gui/stage_metadata.py.</summary>
public sealed class StageMetadata : IStage
{
    private readonly MainWindow _app;

    private CheckBox _noRotate = null!, _noRotateMirror = null!, _north = null!, _south = null!, _equator = null!, _continent = null!, _fantasy = null!, _uniqueEnabled = null!;
    private TextBox _uniqueValue = null!, _weightBox = null!;
    private Border _emptySwatch = null!;

    private ListBox _extraList = null!;

    public UIElement View { get; }

    public StageMetadata(MainWindow app)
    {
        _app = app;

        var body = Ui.Stack();
        BuildFlags(body);
        BuildWeightEmpty(body);
        BuildExtra(body);

        View = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(8), Child = body },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    public void OnShow()
    {
        RefreshExtra();
    }

    // -- flags --------------------------------------------------------------

    private void BuildFlags(StackPanel body)
    {
        var m = _app.Project!.Metadata;
        var f = Ui.Stack();

        _noRotate = Ui.CheckBoxCtl("do_not_rotate", m.DoNotRotate, (_, _) => Sync());
        _noRotateMirror = Ui.CheckBoxCtl("do_not_rotate_or_mirror", m.DoNotRotateOrMirror, (_, _) => Sync());
        _north = Ui.CheckBoxCtl("restrict_to_north_edge", m.RestrictToNorthEdge, (_, _) => Sync());
        _south = Ui.CheckBoxCtl("restrict_to_south_edge", m.RestrictToSouthEdge, (_, _) => Sync());
        _equator = Ui.CheckBoxCtl("restrict_to_equator", m.RestrictToEquator, (_, _) => Sync());
        _continent = Ui.CheckBoxCtl("continent (recommended for tiles 5x5 or bigger)", m.Continent, (_, _) => Sync());
        _fantasy = Ui.CheckBoxCtl("fantasy", m.Fantasy, (_, _) => Sync());
        f.Children.Add(_noRotate);
        f.Children.Add(_noRotateMirror);
        f.Children.Add(_north);
        f.Children.Add(_south);
        f.Children.Add(_equator);
        f.Children.Add(_continent);
        f.Children.Add(_fantasy);

        var uf = Ui.Horizontal();
        _uniqueEnabled = Ui.CheckBoxCtl("unique =", m.Unique.HasValue, (_, _) => Sync());
        _uniqueValue = new TextBox { Width = 50, Text = (m.Unique ?? -1).ToString(), Margin = new Thickness(6, 0, 0, 0) };
        _uniqueValue.TextChanged += (_, _) => Sync();
        uf.Children.Add(_uniqueEnabled);
        uf.Children.Add(_uniqueValue);
        f.Children.Add(uf);

        body.Children.Add(Ui.Group("Rotation / placement flags", f));
    }

    private void BuildWeightEmpty(StackPanel body)
    {
        var m = _app.Project!.Metadata;
        var f = Ui.Stack();

        var wf = Ui.Horizontal();
        wf.Children.Add(new TextBlock { Text = "weight = ", VerticalAlignment = VerticalAlignment.Center });
        _weightBox = new TextBox { Width = 60, Text = m.Weight.ToString() };
        _weightBox.TextChanged += (_, _) => Sync();
        wf.Children.Add(_weightBox);
        wf.Children.Add(new TextBlock { Text = "  (100 = standard chance to appear)", VerticalAlignment = VerticalAlignment.Center });
        f.Children.Add(wf);

        var ef = Ui.Horizontal();
        ef.Children.Add(new TextBlock { Text = "empty color (randomized-seazone background): ", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Width = 150 });
        _emptySwatch = new Border { Width = 24, Height = 18, BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), Background = new SolidColorBrush(Color.FromRgb(m.EmptyColor.R, m.EmptyColor.G, m.EmptyColor.B)), Margin = new Thickness(6, 0, 6, 0) };
        ef.Children.Add(_emptySwatch);
        ef.Children.Add(Ui.Button("Choose...", (_, _) => PickEmptyColor()));
        f.Children.Add(ef);

        body.Children.Add(Ui.Group("Weight / empty color", f));
    }

    private void PickEmptyColor()
    {
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true };
        var c = _app.Project!.Metadata.EmptyColor;
        dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        var rgb = new Rgb(dlg.Color.R, dlg.Color.G, dlg.Color.B);
        _app.Project.Metadata.EmptyColor = rgb;
        _emptySwatch.Background = new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B));
        if (_app.Project.ProvinceResult != null)
            ProvinceMapGen.Recolor(_app.Project.ProvinceResult, rgb);
    }

    private void Sync()
    {
        var m = _app.Project!.Metadata;
        m.DoNotRotate = _noRotate.IsChecked ?? false;
        m.DoNotRotateOrMirror = _noRotateMirror.IsChecked ?? false;
        m.RestrictToNorthEdge = _north.IsChecked ?? false;
        m.RestrictToSouthEdge = _south.IsChecked ?? false;
        m.RestrictToEquator = _equator.IsChecked ?? false;
        m.Continent = _continent.IsChecked ?? false;
        m.Fantasy = _fantasy.IsChecked ?? false;
        m.Unique = (_uniqueEnabled.IsChecked ?? false) && int.TryParse(_uniqueValue.Text, out var uv) ? uv : null;
        if (int.TryParse(_weightBox.Text, out var wv)) m.Weight = wv;
    }

    // -- extra passthrough fields -------------------------------------------------

    private void BuildExtra(StackPanel body)
    {
        var f = Ui.Stack();
        _extraList = new ListBox { Height = 80 };
        f.Children.Add(_extraList);
        var bf = Ui.Horizontal();
        bf.Children.Add(Ui.Button("Add...", (_, _) => AddExtra()));
        bf.Children.Add(Ui.Button("Remove selected", (_, _) => RemoveExtra()));
        f.Children.Add(bf);
        body.Children.Add(Ui.Group("Extra fields (advanced, passed through as-is, e.g. add_moisture)", f));
        RefreshExtra();
    }

    private void RefreshExtra()
    {
        _extraList.Items.Clear();
        foreach (var (k, v) in _app.Project!.Metadata.ExtraFields)
            _extraList.Items.Add($"{k} = {v}");
    }

    private void AddExtra()
    {
        var result = Dialogs.TwoText(_app, "Extra field", "Field name:", "Value:");
        if (result == null) return;
        var (key, value) = result.Value;
        var fields = _app.Project!.Metadata.ExtraFields;
        int existing = fields.FindIndex(kv => kv.Key == key);
        if (existing >= 0) fields[existing] = (key, value);
        else fields.Add((key, value));
        RefreshExtra();
    }

    private void RemoveExtra()
    {
        int idx = _extraList.SelectedIndex;
        if (idx < 0) return;
        _app.Project!.Metadata.ExtraFields.RemoveAt(idx);
        RefreshExtra();
    }
}
