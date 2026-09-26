using System;
using System.Windows;
using System.Windows.Controls;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Mountains &amp; height stage: paint the mountain mask, generate
/// the height map from it, then optionally hand-touch-up the generated
/// height. Port of gui/stage_mountains.py.</summary>
public sealed class StageMountains : IStage
{
    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _info;

    private string _previewMode = "mountains"; // "mountains" | "height"
    private string _tool = "mountain";          // "mountain" | "erase" | "height" | "raise"
    private readonly BrushSizePanel _brush;
    private double _intensity = 0.6;
    private double _heightValue = 150;
    private double _raiseAmount = 20;

    private readonly Slider _landRise, _interiorH, _mountainBoost, _seaFade, _blurCoast, _blurMountains;
    /// <summary>Defensive guard (see StageRivers' equivalent _painting
    /// field): guarantees a brush stroke's undo snapshot can only ever be
    /// taken once, exactly on its Down event, even if a Drag somehow
    /// arrived without a matching Down first.</summary>
    private bool _strokeActive;

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    public StageMountains(MainWindow app)
    {
        _app = app;
        var s = app.Project!.Settings;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold(Loc.T("mountains.title")));
        side.Children.Add(Ui.Wrap("Paint mountain ridges on land, then generate a starting height map. Fine-tune afterwards with the manual height brush - this is exactly the workflow the tile-making guide recommends.", 260));

        var previewPanel = Ui.Stack();
        previewPanel.Children.Add(Ui.Radio(Loc.T("mountains.preview.mask"), "mountains-preview", true, (_, _) => { _previewMode = "mountains"; Redraw(); }));
        previewPanel.Children.Add(Ui.Radio(Loc.T("mountains.preview.height"), "mountains-preview", false, (_, _) => { _previewMode = "height"; Redraw(); }));
        side.Children.Add(Ui.Group("Preview", previewPanel));

        var toolPanel = Ui.Stack();
        toolPanel.Children.Add(Ui.Radio(Loc.T("mountains.tool.paint"), "mountains-tool", true, (_, _) => _tool = "mountain"));
        toolPanel.Children.Add(Ui.Radio(Loc.T("mountains.tool.erase"), "mountains-tool", false, (_, _) => _tool = "erase"));
        toolPanel.Children.Add(Ui.Radio(Loc.T("mountains.tool.height"), "mountains-tool", false, (_, _) => _tool = "height"));
        toolPanel.Children.Add(Ui.Radio(Loc.T("mountains.tool.raise"), "mountains-tool", false, (_, _) => _tool = "raise"));
        side.Children.Add(Ui.Group("Tool", toolPanel));

        _brush = new BrushSizePanel("Brush size (px)", 0, 300, 25, "mountains",
            new double[] { 4, 25, 60, 120 },
            "Wie groß der Pinsel beim Malen von Gebirgen bzw. beim manuellen Höhen-Touch-up ist. Mit dem Mauszeiger über der Karte wird die aktuelle Größe als Kreis angezeigt.");
        side.Children.Add(_brush.View);
        Ui.LabeledSlider(side, "Mountain intensity", 0.05, 1.0, _intensity, (_, e) => _intensity = e.NewValue, "Wie stark ein Pinselstrich den Gebirgs-Wert anhebt (255 = maximale Gebirgshöhe).");
        // Slider + editable exact-value textbox, kept in sync both ways, so
        // the exact currently-selected height number is always visible on
        // screen (previously only inferable from the slider handle position).
        Ui.LabeledSliderWithValue(side, "Manual height value (96-235)", 50, 235, _heightValue, v => _heightValue = v, "Der exakte Höhenwert, der beim manuellen Höhen-Touch-up aufgetragen wird - die Zahl daneben zeigt den aktuell eingestellten Wert und lässt sich auch direkt eintippen.");
        Ui.LabeledSlider(side, Loc.T("mountains.raiseAmount"), -60, 60, _raiseAmount, (_, e) => _raiseAmount = e.NewValue, Loc.T("mountains.raiseAmount.tip"));

        var gen = Ui.Stack();
        _landRise = Ui.LabeledSlider(gen, "Land rise distance", 10, 200, s.LandRiseDistance, null,
            "Über wie viele Pixel die Höhe von der Küste ins Landesinnere ansteigt. Größere Werte = sanftere, weiter ausgedehnte Steigung.");
        _interiorH = Ui.LabeledSlider(gen, "Interior height", 96, 235, s.LandInteriorHeight, null,
            "Ziel-Höhenwert, den normales Landesinneres (ohne Gebirge) erreicht.");
        _mountainBoost = Ui.LabeledSlider(gen, "Mountain boost", 0, 140, s.MountainBoost, null,
            "Wie viel zusätzliche Höhe an den gemalten Gebirgsstellen oben draufkommt.");
        _seaFade = Ui.LabeledSlider(gen, "Sea fade distance", 5, 150, s.SeaFadeDistance, null,
            "Über wie viele Pixel die Höhe zur Küste hin bzw. im Meer nach unten ausläuft.");
        _blurCoast = Ui.LabeledSlider(gen, Loc.T("mountains.blurCoastlines"), 0, 8, s.BlurCoastlines, null, Loc.T("mountains.blurCoastlines.tip"));
        _blurMountains = Ui.LabeledSlider(gen, Loc.T("mountains.blurMountains"), 0, 12, s.BlurMountains, null, Loc.T("mountains.blurMountains.tip"));
        side.Children.Add(Ui.Group("Auto-generation settings", gen));

        side.Children.Add(Ui.Button(Loc.T("mountains.generate"), (_, _) => Generate()));
        side.Children.Add(Ui.Button(Loc.T("mountains.clear"), (_, _) => ClearMountains()));
        side.Children.Add(Ui.Button("↶ " + Loc.T("common.undo") + " (Ctrl+Z)", (_, _) => _app.Undo?.Undo()));

        _info = Ui.Info(260);
        side.Children.Add(_info);

        _canvas = new PaintCanvasControl
        {
            ImageProvider = rect => _previewMode == "height"
                ? Render.CompositeHeight(_app.Project!, rect)
                : Render.CompositeMountains(_app.Project!, rect, true),
        };
        _canvas.OnPaint = OnPaint;
        _brush.Changed += () => _canvas.BrushPreviewRadius = _brush.EffectiveRadius;
        _canvas.BrushPreviewRadius = _brush.EffectiveRadius;

        View = Ui.SideAndCanvas(Ui.Sidebar(260, side), _canvas);
    }

    private void Redraw()
    {
        _canvas.RequestRedraw();
        var p = _app.Project!;
        string haveHeight = p.Height != null ? "yes" : "no (click generate)";
        _info.Text = $"Height map generated: {haveHeight}";
    }

    public void OnShow()
    {
        var p = _app.Project!;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        _canvas.BrushPreviewRadius = _brush.EffectiveRadius;
        Redraw();
    }

    private void SyncSettings()
    {
        var s = _app.Project!.Settings;
        s.LandRiseDistance = _landRise.Value;
        s.LandInteriorHeight = _interiorH.Value;
        s.MountainBoost = _mountainBoost.Value;
        s.SeaFadeDistance = _seaFade.Value;
        s.BlurCoastlines = _blurCoast.Value;
        s.BlurMountains = _blurMountains.Value;
    }

    private void Generate()
    {
        _app.Undo?.SnapshotBeforeChange();
        SyncSettings();
        _app.Project!.GenerateHeight();
        _previewMode = "height";
        Redraw();
    }

    private void ClearMountains()
    {
        _app.Undo?.SnapshotBeforeChange();
        var m = _app.Project!.MountainMask;
        Array.Clear(m.Data, 0, m.Data.Length);
        Redraw();
    }

    private void OnPaint(PaintEventKind kind, double ix, double iy)
    {
        if (kind == PaintEventKind.Up) { _strokeActive = false; return; }
        if (kind != PaintEventKind.Down && kind != PaintEventKind.Drag) return;
        var p = _app.Project!;

        if (kind == PaintEventKind.Down)
        {
            if ((_tool == "height" || _tool == "raise") && p.Height == null) p.GenerateHeight();
            _app.Undo?.SnapshotBeforeChange();
            _strokeActive = true;
        }
        if (!_strokeActive) return; // a Drag arriving without its Down first must never paint without a snapshot behind it

        double radius = _brush.EffectiveRadius;
        switch (_tool)
        {
            case "mountain":
                RasterOps.PaintMax(p.MountainMask, ix, iy, radius, (byte)Math.Clamp(Math.Round(_intensity * 255.0), 0, 255));
                break;
            case "erase":
                RasterOps.PaintValue(p.MountainMask, ix, iy, radius, 0);
                break;
            case "height":
                if (p.Height == null) p.GenerateHeight();
                RasterOps.PaintValue(p.Height!, ix, iy, radius, (byte)Math.Clamp(Math.Round(_heightValue), 0, 255));
                break;
            case "raise":
                // Additive, soft-edged sculpting brush (as opposed to
                // "height"'s hard set-to-an-exact-value tool) - a negative
                // amount lowers instead, and repeated passes over the same
                // spot while dragging keep accumulating, same as any real
                // terrain-sculpting brush.
                if (p.Height == null) p.GenerateHeight();
                RasterOps.PaintAddClamped(p.Height!, ix, iy, radius, _raiseAmount);
                break;
        }
        Redraw();
    }
}
