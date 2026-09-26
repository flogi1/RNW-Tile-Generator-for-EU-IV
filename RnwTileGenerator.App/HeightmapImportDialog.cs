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
/// "Import an unfinished heightmap" entry point: pick a tile size, pick a
/// source image (a roughly-sketched grayscale height field with thin
/// pencil-line mountain ridges), tune the ridge widen/blur parameters
/// against a fast low-res preview, then hand the result to
/// TileProject.ImportHeightmap and open the normal editor - the user
/// finishes rivers/provinces by hand from there, exactly like a
/// hand-painted coastline would be.
/// </summary>
public sealed class HeightmapImportDialog : Window
{
    public (string name, int gw, int gh, string imagePath, TileProject.HeightmapImportOptions options)? Result;

    private readonly TextBox _nameBox;
    private readonly ComboBox _wBox, _hBox;
    private readonly TextBlock _fileInfo;
    private readonly Image _previewImage;
    private readonly TextBlock _previewStatus;
    private readonly Slider _seaLevel, _edgeThresh, _widen, _corridor, _blur, _mtnThresh;

    private string? _imagePath;
    private (int w, int h)? _sourceSize;

    public HeightmapImportDialog(Window owner)
    {
        Title = Loc.T("heightmap.title");
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var root = new StackPanel { Margin = new Thickness(12), Width = 460 };

        root.Children.Add(new TextBlock
        {
            Text = Loc.T("heightmap.intro"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        root.Children.Add(new TextBlock { Text = Loc.T("heightmap.tileName"), Margin = new Thickness(0, 0, 0, 2) });
        _nameBox = new TextBox { Text = "importedtile" };
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
        _seaLevel = Ui.LabeledSlider(sliderPanel, Loc.T("heightmap.seaLevel"), 0, 255, 90, null, Loc.T("heightmap.seaLevel.tip"));
        _edgeThresh = Ui.LabeledSlider(sliderPanel, Loc.T("heightmap.edgeThreshold"), 2, 80, 20, null, Loc.T("heightmap.edgeThreshold.tip"));
        _widen = Ui.LabeledSlider(sliderPanel, Loc.T("heightmap.lineWiden"), 1, 40, 9, null, Loc.T("heightmap.lineWiden.tip"));
        _corridor = Ui.LabeledSlider(sliderPanel, Loc.T("heightmap.corridor"), 1, 80, 20, null, Loc.T("heightmap.corridor.tip"));
        _blur = Ui.LabeledSlider(sliderPanel, Loc.T("heightmap.blur"), 1, 40, 12, null, Loc.T("heightmap.blur.tip"));
        _mtnThresh = Ui.LabeledSlider(sliderPanel, Loc.T("heightmap.mountainThreshold"), 50, 250, 150, null, Loc.T("heightmap.mountainThreshold.tip"));
        root.Children.Add(Ui.Group(Loc.T("heightmap.paramsGroup"), sliderPanel));

        root.Children.Add(Ui.Button(Loc.T("heightmap.preview"), (_, _) => UpdatePreview()));
        _previewImage = new Image { Width = 420, Height = 260, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 2) };
        RenderOptions.SetBitmapScalingMode(_previewImage, BitmapScalingMode.NearestNeighbor);
        var previewBorder = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _previewImage, Background = Brushes.Black };
        root.Children.Add(previewBorder);
        _previewStatus = new TextBlock { Text = "", Margin = new Thickness(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(_previewStatus);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var createBtn = new Button { Content = Loc.T("heightmap.create"), Width = 130, Margin = new Thickness(4, 0, 4, 0) };
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
            Dialogs.Error(this, Loc.T("heightmap.title"), exc.Message);
            return;
        }
        _imagePath = ofd.FileName;
        _fileInfo.Text = $"{System.IO.Path.GetFileName(ofd.FileName)}  ({_sourceSize.Value.w}x{_sourceSize.Value.h}px)";
    }

    private (int gw, int gh) SelectedGrid() => ((int)(_wBox.SelectedItem ?? 4), (int)(_hBox.SelectedItem ?? 4));

    private TileProject.HeightmapImportOptions BuildOptions(double scale = 1.0) => new()
    {
        SeaLevel = (int)Math.Round(_seaLevel.Value),
        EdgeThreshold = (int)Math.Round(_edgeThresh.Value),
        LineWidenRadius = Math.Max(1, (int)Math.Round(_widen.Value * scale)),
        CorridorRadius = Math.Max(1, (int)Math.Round(_corridor.Value * scale)),
        BlurSigma = Math.Max(0.5, _blur.Value * scale),
        MountainHeightThreshold = (int)Math.Round(_mtnThresh.Value),
    };

    private void UpdatePreview()
    {
        if (_imagePath == null)
        {
            Dialogs.Warn(this, Loc.T("heightmap.title"), Loc.T("heightmap.noFileWarning"));
            return;
        }
        var (gw, gh) = SelectedGrid();
        int fullW = gw * Constants.GridUnit, fullH = gh * Constants.GridUnit;

        int previewW = Math.Min(420, fullW);
        int previewH = Math.Max(1, (int)Math.Round(previewW * (fullH / (double)fullW)));
        double scale = previewW / (double)fullW; // shrink the widen/corridor/blur radii to match, so the preview looks representative of the full-res result

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var gray = ImageIO.LoadGrayscaleResized(_imagePath, previewW, previewH);
            var opts = BuildOptions(scale);

            var land = GrayMap.Bool(previewW, previewH, false);
            for (int i = 0; i < gray.Data.Length; i++)
                if (gray.Data[i] >= opts.SeaLevel) land.Data[i] = 255;

            var seaMask = GrayMap.Not(land);
            var widened = RasterOps.WidenAndBlurRidgeLines(gray, seaMask, opts.EdgeThreshold, opts.LineWidenRadius, opts.CorridorRadius, opts.BlurSigma);

            var mtn = new GrayMap(previewW, previewH);
            int span = Math.Max(255 - opts.MountainHeightThreshold, 1);
            for (int i = 0; i < widened.Data.Length; i++)
            {
                if (land.Data[i] < 128) continue;
                int v = widened.Data[i];
                if (v <= opts.MountainHeightThreshold) continue;
                mtn.Data[i] = (byte)Math.Clamp((v - opts.MountainHeightThreshold) * 255 / span, 0, 255);
            }

            var rgb = ImageIO.PreviewRgb(land, mtn);
            var bmp = new WriteableBitmap(previewW, previewH, 96, 96, PixelFormats.Rgb24, null);
            bmp.WritePixels(new Int32Rect(0, 0, previewW, previewH), rgb, previewW * 3, 0);
            bmp.Freeze();
            _previewImage.Source = bmp;

            int landPx = land.CountTrue(), mtnPx = 0;
            for (int i = 0; i < mtn.Data.Length; i++) if (mtn.Data[i] > 0) mtnPx++;
            _previewStatus.Text = string.Format(Loc.T("heightmap.previewStatus"), 100.0 * landPx / land.Data.Length, 100.0 * mtnPx / Math.Max(landPx, 1));
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, Loc.T("heightmap.title"), exc.Message);
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
            Dialogs.Error(this, Loc.T("heightmap.title"), Loc.T("newtile.needName"));
            return;
        }
        if (_imagePath == null)
        {
            Dialogs.Warn(this, Loc.T("heightmap.title"), Loc.T("heightmap.noFileWarning"));
            return;
        }
        var (gw, gh) = SelectedGrid();
        try
        {
            TileProject.ValidateGridSize(gw, gh);
        }
        catch (Exception ex)
        {
            Dialogs.Error(this, Loc.T("heightmap.title"), ex.Message);
            return;
        }
        Result = (name, gw, gh, _imagePath, BuildOptions());
        Close();
    }
}
