using System;
using System.Windows;
using System.Windows.Controls;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Coastline stage: paint land vs. sea/lake. Port of
/// gui/stage_coastline.py.</summary>
public sealed class StageCoastline : IStage
{
    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _info;

    private string _tool = "land"; // "land" | "water" | "fill"
    private readonly BrushSizePanel _brush;
    private bool _showGrid = true;

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    public StageCoastline(MainWindow app)
    {
        _app = app;

        // Built before the side panel below (a "Fit to window" button
        // there needs to reference it) so the compiler's nullable flow
        // analysis sees the readonly field as definitely assigned at the
        // point that lambda captures it.
        _canvas = new PaintCanvasControl
        {
            ImageProvider = rect => Render.CompositeCoastline(_app.Project!, rect, _showGrid),
        };
        _canvas.OnPaint = OnPaint;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold(Loc.T("coastline.title")));
        side.Children.Add(Ui.Wrap("Paint dry land. Everything left unpainted is water; the program later works out on its own which water is open sea and which is a fully enclosed lake."));

        side.Children.Add(Ui.Radio(Loc.T("coastline.tool.land"), "coastline-tool", true, (_, _) => { _tool = "land"; UpdateBrushPreview(); }));
        side.Children.Add(Ui.Radio(Loc.T("coastline.tool.water"), "coastline-tool", false, (_, _) => { _tool = "water"; UpdateBrushPreview(); }));
        side.Children.Add(Ui.Radio(Loc.T("coastline.tool.fill"), "coastline-tool", false, (_, _) => { _tool = "fill"; UpdateBrushPreview(); }));

        _brush = new BrushSizePanel("Brush size (px)", 0, 300, 12, "coastline",
            new double[] { 4, 12, 30, 80 },
            "Wie groß der Pinsel beim Malen von Land/Wasser ist. Mit dem Mauszeiger über der Karte wird die aktuelle Größe als Kreis angezeigt.");
        _brush.Changed += UpdateBrushPreview;
        side.Children.Add(_brush.View);

        side.Children.Add(Ui.CheckBoxCtl(Loc.T("coastline.showGrid"), _showGrid, (s, _) =>
        {
            _showGrid = ((CheckBox)s).IsChecked ?? false;
            Redraw();
        }));

        side.Children.Add(Ui.Button(Loc.T("common.fitWindow"), (_, _) => _canvas.FitToWindow()));
        side.Children.Add(Ui.Button("↶ " + Loc.T("common.undo") + " (Ctrl+Z)", (_, _) => _app.Undo?.Undo()));

        _info = Ui.Info();
        side.Children.Add(_info);

        View = Ui.SideAndCanvas(Ui.Sidebar(230, side), _canvas);
        UpdateBrushPreview();
    }

    private void UpdateBrushPreview() =>
        _canvas.BrushPreviewRadius = _tool == "fill" ? null : _brush.EffectiveRadius;

    private void Redraw()
    {
        _canvas.RequestRedraw();
        UpdateInfo();
    }

    public void OnShow()
    {
        var p = _app.Project!;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        UpdateBrushPreview();
        Redraw();
    }

    private void UpdateInfo()
    {
        var p = _app.Project!;
        long total = (long)p.WidthPx * p.HeightPx;
        int land = p.LandMask.CountTrue();
        double pct = total > 0 ? 100.0 * land / total : 0.0;
        _info.Text = $"Tile: {p.WidthPx}x{p.HeightPx}px\nLand: {land} px ({pct:0.0}%)";
    }

    private void OnPaint(PaintEventKind kind, double ix, double iy)
    {
        if (kind == PaintEventKind.Hover) return;
        var p = _app.Project!;
        if (kind == PaintEventKind.Down) _app.Undo?.SnapshotBeforeChange();

        if (_tool == "fill")
        {
            if (kind != PaintEventKind.Down) return;
            int x = (int)ix, y = (int)iy;
            var land = p.LandMask;
            if (!land.InBounds(x, y)) return;
            bool current = land.GetBool(x, y);
            var region = RasterOps.FloodFillBool(land, x, y);
            for (int i = 0; i < land.Data.Length; i++)
                if (region.Data[i] >= 128) land.Data[i] = current ? (byte)0 : (byte)255;
            // A fill can turn a large area of previously-water pixels into
            // land in one go - if a height map already exists, make sure
            // none of it silently reads as underwater (see
            // ClampHeightToLandSafety's own doc comment, Runde 7).
            p.ClampHeightToLandSafety();
        }
        else
        {
            bool value = _tool == "land";
            RasterOps.PaintBoolBrush(p.LandMask, ix, iy, _brush.EffectiveRadius, value);
            // Only at stroke end, not every drag point - a full-tile height
            // scan on every mouse-move would be wasteful.
            if (kind == PaintEventKind.Up) p.ClampHeightToLandSafety();
        }
        Redraw();
    }
}
