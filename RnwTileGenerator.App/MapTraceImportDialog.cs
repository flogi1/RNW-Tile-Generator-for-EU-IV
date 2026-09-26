using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>
/// "Kartenbild-Tracing" (Runde 8) entry point: pick a tile size, pick a
/// reference map image (a real-world map, a screenshot, a hand-drawn
/// sketch - anything where land and water are visually distinguishable),
/// tune a brightness threshold against a fast low-res preview, then hand
/// the result to TileProject.ImportMapTrace and open the normal editor -
/// same "derive coastline, finish the rest by hand" workflow as
/// HeightmapImportDialog, just without any mountain-ridge detection (a
/// reference map has no elevation encoding to detect ridges from).
/// </summary>
public sealed class MapTraceImportDialog : Window
{
    public (string name, int gw, int gh, string imagePath, TileProject.MapTraceOptions options)? Result;

    private readonly TextBox _nameBox;
    private readonly ComboBox _wBox, _hBox;
    private readonly TextBlock _fileInfo;
    private readonly Image _previewImage;
    private readonly TextBlock _previewStatus;
    private readonly Slider _threshold;
    private readonly CheckBox _invertCheck;

    private string? _imagePath;
    private (int w, int h)? _sourceSize;

    public MapTraceImportDialog(Window owner)
    {
        Title = Loc.T("mapTrace.title");
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(12), Width = 460 };

        // Runde 9 feedback item 2 ("Markiere das Feature jedenfalls erstmal
        // als 'Experimentell'") - same DarkOrange treatment as the border-
        // curviness slider (StageRandom.cs/StageProvinces.cs): the intro
        // text itself already spells out (in every language) exactly what
        // currently goes wrong (map text, fine coastline detail, freehand
        // scribbles), so this is a visual reinforcement of that, not a
        // separate warning.
        root.Children.Add(new TextBlock
        {
            Text = Loc.T("mapTrace.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = Brushes.DarkOrange,
        });

        root.Children.Add(new TextBlock { Text = Loc.T("heightmap.tileName"), Margin = new Thickness(0, 0, 0, 2) });
        _nameBox = new TextBox { Text = "tracedtile" };
        root.Children.Add(_nameBox);

        var sizeGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.RowDefinitions.Add(new RowDefinition());
        sizeGrid.RowDefinitions.Add(new RowDefinition());
        var wLabel = new TextBlock { Text = Loc.T("newtile.width"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 4) };
        Grid.SetRow(wLabel, 0); Grid.SetColumn(wLabel, 0);
        _wBox = new ComboBox { ItemsSource = Enumerable.Range(1, Constants.MaxTileGridW).ToList(), SelectedItem = 4, Margin = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(_wBox, 0); Grid.SetColumn(_wBox, 1);
        var hLabel = new TextBlock { Text = Loc.T("newtile.height"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Grid.SetRow(hLabel, 1); Grid.SetColumn(hLabel, 0);
        _hBox = new ComboBox { ItemsSource = Enumerable.Range(1, Constants.MaxTileGridH).ToList(), SelectedItem = 4 };
        Grid.SetRow(_hBox, 1); Grid.SetColumn(_hBox, 1);
        sizeGrid.Children.Add(wLabel); sizeGrid.Children.Add(_wBox);
        sizeGrid.Children.Add(hLabel); sizeGrid.Children.Add(_hBox);
        root.Children.Add(sizeGrid);

        root.Children.Add(Ui.Button(Loc.T("heightmap.chooseFile"), (_, _) => ChooseFile()));
        _fileInfo = new TextBlock { Text = Loc.T("heightmap.noFile"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
        root.Children.Add(_fileInfo);

        var sliderPanel = Ui.Stack();
        _threshold = Ui.LabeledSlider(sliderPanel, Loc.T("mapTrace.threshold"), 0, 255, 128, null, Loc.T("mapTrace.threshold.tip"));
        _invertCheck = Ui.CheckBoxCtl(Loc.T("mapTrace.invert"), false, (_, _) => { });
        _invertCheck.ToolTip = Loc.T("mapTrace.invert.tip");
        sliderPanel.Children.Add(_invertCheck);
        root.Children.Add(Ui.Group(Loc.T("mapTrace.paramsGroup"), sliderPanel));

        root.Children.Add(Ui.Button(Loc.T("heightmap.preview"), (_, _) => UpdatePreview()));
        _previewImage = new Image { Width = 420, Height = 260, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 2) };
        RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.NearestNeighbor);
        var previewBorder = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _previewImage, Background = Brushes.Black };
        root.Children.Add(previewBorder);
        _previewStatus = new TextBlock { Text = "", Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_previewStatus);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var createBtn = new Button { Content = Loc.T("mapTrace.create"), Width = 150, Margin = new Thickness(4, 0, 4, 0) };
        createBtn.Click += (_, _) => OnCreate();
        var cancelBtn = new Button { Content = Loc.T("common.cancel"), Width = 90, Margin = new Thickness(4, 0, 4, 0) };
        cancelBtn.Click += (_, _) => Close();
        buttonRow.Children.Add(createBtn);
        buttonRow.Children.Add(cancelBtn);
        root.Children.Add(buttonRow);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 700 };
    }

    private void ChooseFile()
    {
        var ofd = new OpenFileDialog
        {
            Title = Loc.T("heightmap.chooseFile"),
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true) return;
        try
        {
            _sourceSize = ImageIO.ReadSize(ofd.FileName);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, Loc.T("mapTrace.title"), exc.Message);
            return;
        }
        _imagePath = ofd.FileName;
        _fileInfo.Text = $"{System.IO.Path.GetFileName(ofd.FileName)}  ({_sourceSize.Value.w}x{_sourceSize.Value.h}px)";
    }

    private (int gw, int gh) SelectedGrid() => ((int)(_wBox.SelectedItem ?? 4), (int)(_hBox.SelectedItem ?? 4));

    private TileProject.MapTraceOptions BuildOptions() => new()
    {
        Threshold = (int)Math.Round(_threshold.Value),
        Invert = _invertCheck.IsChecked ?? false,
    };

    private void UpdatePreview()
    {
        if (_imagePath == null)
        {
            Dialogs.Warn(this, Loc.T("mapTrace.title"), Loc.T("mapTrace.noFileWarning"));
            return;
        }
        var (gw, gh) = SelectedGrid();
        int fullW = gw * Constants.GridUnit, fullH = gh * Constants.GridUnit;

        int previewW = Math.Min(420, fullW);
        int previewH = Math.Max(1, (int)Math.Round(previewW * (fullH / (double)fullW)));

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var gray = ImageIO.LoadGrayscaleResized(_imagePath, previewW, previewH);
            var opts = BuildOptions();

            var land = GrayMap.Bool(previewW, previewH, false);
            for (int i = 0; i < gray.Data.Length; i++)
            {
                bool bright = gray.Data[i] >= opts.Threshold;
                if (bright != opts.Invert) land.Data[i] = 255;
            }

            // No mountain data to show for a traced reference map - an
            // all-zero mask reuses the same land/sea preview colors
            // ImageIO.PreviewRgb already draws for HeightmapImportDialog.
            var noMountain = new GrayMap(previewW, previewH);
            var rgb = ImageIO.PreviewRgb(land, noMountain);
            var bmp = new WriteableBitmap(previewW, previewH, 96, 96, PixelFormats.Rgb24, null);
            bmp.WritePixels(new Int32Rect(0, 0, previewW, previewH), rgb, previewW * 3, 0);
            bmp.Freeze();
            _previewImage.Source = bmp;

            int landPx = land.CountTrue();
            _previewStatus.Text = string.Format(Loc.T("mapTrace.previewStatus"), 100.0 * landPx / land.Data.Length);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, Loc.T("mapTrace.title"), exc.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    private void OnCreate()
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Dialogs.Error(this, Loc.T("mapTrace.title"), Loc.T("newtile.needName"));
            return;
        }
        if (_imagePath == null)
        {
            Dialogs.Warn(this, Loc.T("mapTrace.title"), Loc.T("mapTrace.noFileWarning"));
            return;
        }
        var (gw, gh) = SelectedGrid();
        try
        {
            TileProject.ValidateGridSize(gw, gh);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, Loc.T("mapTrace.title"), ex.Message);
            return;
        }
        Result = (name, gw, gh, _imagePath, BuildOptions());
        Close();
    }
}
