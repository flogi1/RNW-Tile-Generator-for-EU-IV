using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Direct port of the Python NewTileDialog (bottom of gui/app.py).</summary>
public sealed class NewTileDialog : Window
{
    public (string name, int gw, int gh)? Result;

    private readonly TextBox _nameBox;
    private readonly ComboBox _wBox;
    private readonly ComboBox _hBox;
    private readonly TextBlock _info;

    public NewTileDialog(Window owner)
    {
        Title = Loc.T("newtile.title");
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var body = new StackPanel { Margin = new Thickness(12), Width = 340 };
        body.Children.Add(new TextBlock { Text = Loc.T("newtile.name"), Margin = new Thickness(0, 0, 0, 2) });
        _nameBox = new TextBox { Text = "newtile" };
        body.Children.Add(_nameBox);

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());

        var wLabel = new TextBlock { Text = Loc.T("newtile.width"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) };
        Grid.SetRow(wLabel, 0); Grid.SetColumn(wLabel, 0);
        _wBox = new ComboBox { ItemsSource = Enumerable.Range(1, Constants.MaxTileGridW).ToList(), SelectedItem = 4, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(_wBox, 0); Grid.SetColumn(_wBox, 1);

        var hLabel = new TextBlock { Text = Loc.T("newtile.height"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetRow(hLabel, 1); Grid.SetColumn(hLabel, 0);
        _hBox = new ComboBox { ItemsSource = Enumerable.Range(1, Constants.MaxTileGridH).ToList(), SelectedItem = 4 };
        Grid.SetRow(_hBox, 1); Grid.SetColumn(_hBox, 1);

        grid.Children.Add(wLabel);
        grid.Children.Add(_wBox);
        grid.Children.Add(hLabel);
        grid.Children.Add(_hBox);
        body.Children.Add(grid);

        _info = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
        body.Children.Add(_info);

        _wBox.SelectionChanged += (_, _) => UpdateInfo();
        _hBox.SelectionChanged += (_, _) => UpdateInfo();
        UpdateInfo();

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
        var createBtn = new Button { Content = Loc.T("newtile.create"), Width = 90, Margin = new Thickness(4, 0, 4, 0) };
        createBtn.Click += (_, _) => OnCreate();
        var cancelBtn = new Button { Content = Loc.T("common.cancel"), Width = 90, Margin = new Thickness(4, 0, 4, 0) };
        cancelBtn.Click += (_, _) => Close();
        buttonRow.Children.Add(createBtn);
        buttonRow.Children.Add(cancelBtn);
        body.Children.Add(buttonRow);

        Content = body;
    }

    private void UpdateInfo()
    {
        int gw = (int)(_wBox.SelectedItem ?? 4);
        int gh = (int)(_hBox.SelectedItem ?? 4);
        int pxW = gw * Constants.GridUnit, pxH = gh * Constants.GridUnit;
        int cells = gw * gh;
        int estLand = (int)(cells * Constants.LandProvincesPerCellDefault);
        _info.Text =
            $"Pixel size: {pxW} x {pxH}\n" +
            $"Whole Random New World is {Constants.RnwGridW}x{Constants.RnwGridH} cells " +
            $"({Constants.RnwGridW * Constants.GridUnit}x{Constants.RnwGridH * Constants.GridUnit}px).\n" +
            $"At the default density that's roughly {estLand} land provinces before adding sea - keep a total under ~{Constants.ProvinceHardCap}.";
    }

    private void OnCreate()
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Dialogs.Error(this, Loc.T("newtile.title"), Loc.T("newtile.needName"));
            return;
        }
        int gw = (int)(_wBox.SelectedItem ?? 4);
        int gh = (int)(_hBox.SelectedItem ?? 4);
        try
        {
            TileProject.ValidateGridSize(gw, gh);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, Loc.T("newtile.title"), ex.Message);
            return;
        }
        Result = (name, gw, gh);
        Close();
    }
}
