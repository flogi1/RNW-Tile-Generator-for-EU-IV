using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Rivers stage: click out polylines to build a river network.
/// Port of gui/stage_rivers.py.</summary>
public sealed class StageRivers : IStage
{
    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _info;
    private readonly ListBox _segList;
    private readonly Button _discardBtn;

    private readonly List<(int x, int y)> _pendingPoints = new();
    private RiverJunction _junction = RiverJunction.Source;
    private double _size = 3;
    private bool _showGrid = true;
    /// <summary>"waypoints" (click each point, then Finish) or "freehand"
    /// (drag a stroke, every distinct pixel touched is recorded, finishes
    /// on mouse-up) - see StageRivers class doc comment.</summary>
    private string _mode = "freehand";
    private bool _painting;
    private bool _refreshingList;
    /// <summary>True while the current segments are the untouched result of the last Regenerate
    /// on this tab: another Regenerate then skips the "replace?" prompt. Cleared by any hand edit
    /// and when the tab is shown again.</summary>
    private bool _riversJustGenerated;
    private readonly Slider _sizeSlider;
    private double _autoRiverCount = GenerationPrefs.GetDouble("rivers.autoCount", 6);
    private readonly Slider _autoRiverCountSlider;
    private double _autoTributaryFrequency = GenerationPrefs.GetDouble("rivers.autoTributaryFrequency", 0);
    private readonly Slider _autoTributaryFrequencySlider;
    private double _autoDistributaryFrequency = GenerationPrefs.GetDouble("rivers.autoDistributaryFrequency", 0);
    private readonly Slider _autoDistributaryFrequencySlider;
    private double _autoMinRiverLength = GenerationPrefs.GetDouble("rivers.autoMinRiverLength", 0);
    private readonly Slider _autoMinRiverLengthSlider;
    private double _autoRiverSmoothness = GenerationPrefs.GetDouble("rivers.autoSmoothness", 6);
    private readonly Slider _autoRiverSmoothnessSlider;

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    /// <remarks>
    /// Two ways to lay down a river: clicking out every waypoint by hand
    /// ("Waypoints" mode, precise but fiddly for a long winding river), or
    /// "Freehand" mode, which treats a mouse drag like a brush stroke -
    /// every distinct pixel the cursor touches is recorded (no minimum
    /// travel distance, so it stays pixel-accurate even mid-stroke), and
    /// releasing the mouse both finishes the segment and immediately starts
    /// fresh. Runde 23 rewrite: Freehand replaces the old separate "Paint"
    /// (sparse-sampled, so its own live preview - a raw line with no
    /// diagonal bridging - could visibly show gaps a fast stroke's export
    /// never actually had) and "Pixel fix" (always an anonymous, unmarked
    /// patch) modes. The junction type radio (Source/Tributary/
    /// Distributary/Patch) now applies to Freehand too, so the same tool
    /// draws a brand new marked river, a tributary/distributary starting
    /// exactly on an existing river pixel, AND an anonymous gap-patch
    /// (Patch skips the "must start on an existing river" check, like
    /// before). The live preview now renders through
    /// RiverMapGen.RasterizeSegment - the exact same Bresenham+bridge+
    /// self-loop-collapse pipeline used for the final export - so what is
    /// seen while dragging is pixel-for-pixel what gets saved, and a stroke
    /// that doubles back over itself visibly collapses instead of leaving
    /// a self-touching cluster. Three quick size presets (Hauptfluss/Fluss/
    /// Bach) sit next to the size slider so switching "river type" before
    /// drawing the next stroke is a single click rather than dragging the
    /// slider to a remembered value.
    /// </remarks>
    public StageRivers(MainWindow app)
    {
        _app = app;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold(Loc.T("rivers.title")));
        side.Children.Add(Ui.Wrap(Loc.T("rivers.intro"), 260));

        var modePanel = Ui.Stack();
        modePanel.Children.Add(Ui.Radio(Loc.T("rivers.mode.freehand"), "river-mode", true, (_, _) => { _mode = "freehand"; CancelPending(); }));
        modePanel.Children.Add(Ui.Radio(Loc.T("rivers.mode.waypoints"), "river-mode", false, (_, _) => { _mode = "waypoints"; CancelPending(); }));
        side.Children.Add(Ui.Group(Loc.T("rivers.mode"), modePanel));

        var jf = Ui.Stack();
        jf.Children.Add(Ui.Radio(Loc.T("rivers.junction.source"), "river-junction", true, (_, _) => _junction = RiverJunction.Source));
        jf.Children.Add(Ui.Radio(Loc.T("rivers.junction.merge"), "river-junction", false, (_, _) => _junction = RiverJunction.Merge));
        jf.Children.Add(Ui.Radio(Loc.T("rivers.junction.split"), "river-junction", false, (_, _) => _junction = RiverJunction.Split));
        var patchRadio = Ui.Radio(Loc.T("rivers.junction.patch"), "river-junction", false, (_, _) => _junction = RiverJunction.Patch);
        patchRadio.ToolTip = Loc.T("rivers.junction.patch.tip");
        jf.Children.Add(patchRadio);
        side.Children.Add(Ui.Group(Loc.T("rivers.junctionGroup"), jf));

        _sizeSlider = Ui.LabeledSlider(side, Loc.T("rivers.size"), Constants.RiverMinSize, Constants.RiverMaxSize, _size, (_, e) => _size = e.NewValue,
            Loc.T("rivers.size.tip"));

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 6) };
        presetRow.Children.Add(RiverTypeButton(Loc.T("rivers.type.main"), 1));
        presetRow.Children.Add(RiverTypeButton(Loc.T("rivers.type.river"), 4));
        presetRow.Children.Add(RiverTypeButton(Loc.T("rivers.type.stream"), 8));
        side.Children.Add(presetRow);

        side.Children.Add(Ui.Button(Loc.T("rivers.finish"), (_, _) => FinishSegment()));
        side.Children.Add(Ui.Button(Loc.T("rivers.cancelPending"), (_, _) => CancelPending()));
        side.Children.Add(Ui.Sep());

        _segList = new ListBox { Height = 160 };
        // Selected list entry is shown in red on the map (see Render.CompositeRivers).
        _segList.SelectionChanged += (_, _) => { if (!_refreshingList) _canvas?.RequestRedraw(); };
        side.Children.Add(_segList);
        side.Children.Add(Ui.Button(Loc.T("rivers.deleteSelected"), (_, _) => DeleteSelected()));
        side.Children.Add(Ui.Button(Loc.T("rivers.clearAll"), (_, _) => ClearAll()));
        side.Children.Add(Ui.Button("↶ " + Loc.T("common.undo") + " (Ctrl+Z)", (_, _) => _app.Undo?.Undo()));
        side.Children.Add(Ui.Sep());

        var autoPanel = Ui.Stack();
        _autoRiverCountSlider = Ui.LabeledSlider(autoPanel, Loc.T("rivers.autoCount"), 1, 30, _autoRiverCount, (_, e) => { _autoRiverCount = e.NewValue; GenerationPrefs.Set("rivers.autoCount", _autoRiverCount); }, Loc.T("rivers.autoCount.tip"));
        _autoTributaryFrequencySlider = Ui.LabeledSlider(autoPanel, Loc.T("random.tributaryFrequency"), 0, 1, _autoTributaryFrequency, (_, e) => { _autoTributaryFrequency = e.NewValue; GenerationPrefs.Set("rivers.autoTributaryFrequency", _autoTributaryFrequency); }, Loc.T("random.tributaryFrequency.tip"));
        _autoDistributaryFrequencySlider = Ui.LabeledSlider(autoPanel, Loc.T("random.distributaryFrequency"), 0, 1, _autoDistributaryFrequency, (_, e) => { _autoDistributaryFrequency = e.NewValue; GenerationPrefs.Set("rivers.autoDistributaryFrequency", _autoDistributaryFrequency); }, Loc.T("random.distributaryFrequency.tip"));
        _autoMinRiverLengthSlider = Ui.LabeledSlider(autoPanel, Loc.T("random.minRiverLength"), 0, 1, _autoMinRiverLength, (_, e) => { _autoMinRiverLength = e.NewValue; GenerationPrefs.Set("rivers.autoMinRiverLength", _autoMinRiverLength); }, Loc.T("random.minRiverLength.tip"));
        _autoRiverSmoothnessSlider = Ui.LabeledSlider(autoPanel, Loc.T("random.riverSmoothness"), 4, 10, _autoRiverSmoothness, (_, e) => { _autoRiverSmoothness = e.NewValue; GenerationPrefs.Set("rivers.autoSmoothness", _autoRiverSmoothness); }, Loc.T("random.riverSmoothness.tip"));
        var regenNewBtn = Ui.Button(Loc.T("rivers.regenNew"), (_, _) => RegenerateRivers(newSeed: true));
        regenNewBtn.ToolTip = Loc.T("rivers.regenNew.tip");
        autoPanel.Children.Add(regenNewBtn);
        var regenSameBtn = Ui.Button(Loc.T("rivers.regenSame"), (_, _) => RegenerateRivers(newSeed: false));
        regenSameBtn.ToolTip = Loc.T("rivers.regenSame.tip");
        autoPanel.Children.Add(regenSameBtn);
        side.Children.Add(Ui.Group(Loc.T("rivers.autoGroup"), autoPanel));

        _discardBtn = Ui.Button(Loc.T("rivers.discardImported"), (_, _) => DiscardImported());
        side.Children.Add(_discardBtn);

        side.Children.Add(Ui.CheckBoxCtl(Loc.T("rivers.showGrid"), _showGrid, (s, _) =>
        {
            _showGrid = ((CheckBox)s).IsChecked ?? false;
            Redraw();
        }));

        _info = Ui.Info(260);
        side.Children.Add(_info);

        _canvas = new PaintCanvasControl { ImageProvider = ProvideImage };
        _canvas.OnPaint = OnPaint;

        View = Ui.SideAndCanvas(Ui.Sidebar(260, side), _canvas);
    }

    // -- rendering --------------------------------------------------------------

    /// <summary>Runde 19 (user feedback: "beim Zeichnen direkt sehen, welche
    /// Flüsse man zeichnet") - the in-progress polyline used to always draw
    /// plain white regardless of the currently selected type, so a
    /// Main/River/Brook/Tributary/Distributary all looked identical while
    /// being drawn and only showed their real color once finished. Uses
    /// the exact same palette CompositeRivers paints a finished river
    /// with, so the pending preview already looks like the real thing.</summary>
    private Rgb PendingColor()
    {
        // A patch never gets a junction marker (RiverJunction.Patch) - the
        // preview shouldn't show one either, regardless of whichever
        // Source/Merge/Split radio was last left selected before switching
        // to Patch.
        if (_junction == RiverJunction.Merge) return Constants.RiverColorMerge;
        if (_junction == RiverJunction.Split) return Constants.RiverColorSplit;
        int idx = Math.Clamp((int)Math.Round(_size), Constants.RiverMinSize, Constants.RiverMaxSize) - 1;
        return Constants.RiverColorsBlue[idx];
    }

    private byte[] ProvideImage(Int32Rect rect)
    {
        int sel = _segList.SelectedIndex;
        var buf = Render.CompositeRivers(_app.Project!, rect, _showGrid, sel >= 0 ? sel : null);
        if (_pendingPoints.Count == 0) return buf;

        int w = rect.Width, h = rect.Height;
        var color = PendingColor();

        if (_mode == "freehand")
        {
            // Render through the exact same pipeline RasterizeSegment/
            // export uses (Bresenham + diagonal bridging + self-loop
            // collapse), instead of a bespoke preview-only line drawer -
            // what is seen while dragging is then pixel-for-pixel what
            // Finish will actually save, including a stroke that doubles
            // back over itself visibly collapsing on the spot rather than
            // leaving a self-touching cluster that only "resolves" later.
            var previewSeg = new RiverSegment { Points = _pendingPoints, Junction = _junction };
            var dense = _pendingPoints.Count >= 2 ? RiverMapGen.RasterizeSegment(previewSeg) : _pendingPoints;
            foreach (var (px, py) in dense)
                SetPx(buf, w, h, px - rect.X, py - rect.Y, color.R, color.G, color.B);

            // Highlight where the junction marker will land (the segment's
            // first point) - skipped for Patch, which never gets one.
            if (_junction != RiverJunction.Patch)
            {
                var mc = _junction switch
                {
                    RiverJunction.Merge => Constants.RiverColorMerge,
                    RiverJunction.Split => Constants.RiverColorSplit,
                    _ => Constants.RiverColorSource,
                };
                StampMarker(buf, w, h, _pendingPoints[0], rect, mc);
            }
        }
        else
        {
            // Waypoints mode: a plain line between clicked points, with a
            // visible dot at each one so the user can see exactly where
            // every click landed before finishing.
            for (int i = 0; i + 1 < _pendingPoints.Count; i++)
            {
                var (ax, ay) = _pendingPoints[i];
                var (bx, by) = _pendingPoints[i + 1];
                foreach (var pt in RiverMapGen.RasterizeSegment(new RiverSegment { Points = new List<(int, int)> { (ax, ay), (bx, by) } }))
                    SetPx(buf, w, h, pt.x - rect.X, pt.y - rect.Y, color.R, color.G, color.B);
            }
            foreach (var pt in _pendingPoints) StampMarker(buf, w, h, pt, rect, color);
        }
        return buf;
    }

    private static void StampMarker(byte[] buf, int w, int h, (int x, int y) center, Int32Rect rect, Rgb color)
    {
        int lx = center.x - rect.X, ly = center.y - rect.Y;
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
                if (dx * dx + dy * dy <= 4) SetPx(buf, w, h, lx + dx, ly + dy, color.R, color.G, color.B);
    }

    private static void SetPx(byte[] buf, int w, int h, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int o = (y * w + x) * 3;
        buf[o] = r; buf[o + 1] = g; buf[o + 2] = b;
    }

    private void Redraw()
    {
        _canvas.RequestRedraw();
        RefreshList();
    }

    public void OnShow()
    {
        var p = _app.Project!;
        _riversJustGenerated = false;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        _discardBtn.IsEnabled = p.ImportedRiverRaster != null;
        Redraw();
    }

    private void RefreshList()
    {
        int keep = _segList.SelectedIndex;
        _refreshingList = true;
        try
        {
            _segList.Items.Clear();
            var segs0 = _app.Project!.RiverSegments;
            for (int i = 0; i < segs0.Count; i++)
            {
                var seg = segs0[i];
                _segList.Items.Add($"#{i}  {seg.Junction,-8}  size {seg.Size}  ({seg.Points.Count} pts)");
            }
            if (keep >= 0 && keep < segs0.Count) _segList.SelectedIndex = keep;
        }
        finally { _refreshingList = false; }
        var segs = _app.Project!.RiverSegments;
        string pending = _pendingPoints.Count > 0 ? $", {_pendingPoints.Count} pending point(s)" : "";
        _info.Text = $"{segs.Count} river segment(s) defined{pending}.";
    }

    // -- interaction --------------------------------------------------------------

    private Button RiverTypeButton(string label, double size)
    {
        var b = new Button { Content = label, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(6, 2, 6, 2) };
        b.ToolTip = Loc.T("rivers.type.tip");
        b.Click += (_, _) => _sizeSlider.Value = size; // ValueChanged updates _size + the slider's own display
        return b;
    }

    /// <summary>A new Merge/Split segment's very first point must land
    /// exactly on an already-drawn river pixel, in both modes - this is
    /// what makes the junction pixel line up when the .txt/.bmp is
    /// exported. Only meaningful for the FIRST point of a fresh segment.</summary>
    private bool ValidateJunctionStart(int x, int y)
    {
        if (_junction != RiverJunction.Merge && _junction != RiverJunction.Split) return true;
        var p = _app.Project!;
        var raster = p.RiverRaster();
        byte v = raster[y * p.WidthPx + x];
        bool onRiver = v != Constants.RiverPalSeaBg && v != Constants.RiverPalLandBg;
        if (!onRiver)
        {
            Dialogs.Info(_app, Loc.T("rivers.title"), Loc.T("rivers.needExistingRiver"));
            return false;
        }
        return true;
    }

    private void OnPaint(PaintEventKind kind, double ix, double iy)
    {
        var p = _app.Project!;
        int x = (int)Math.Round(ix), y = (int)Math.Round(iy);

        if (_mode == "freehand")
        {
            // Pixel-precise brush: every distinct pixel the cursor touches
            // is recorded, no minimum travel distance, so a slow careful
            // drag stays exactly pixel-by-pixel while a fast one still
            // gets every reported position - RasterizeSegment (both for
            // the live preview above and the final export) bridges any gap
            // between two recorded points and collapses any self-touch, so
            // the result is always one connected, cluster-free line
            // regardless of how coarse the recorded points end up. Patch
            // (RiverJunction.Patch) skips the "must start on an existing
            // river" check, same as before; Source/Merge/Split enforce it
            // exactly like Waypoints mode.
            switch (kind)
            {
                case PaintEventKind.Down:
                    if (x < 0 || x >= p.WidthPx || y < 0 || y >= p.HeightPx) return;
                    if (!ValidateJunctionStart(x, y)) return;
                    _painting = true;
                    _pendingPoints.Clear();
                    _pendingPoints.Add((x, y));
                    Redraw();
                    break;

                case PaintEventKind.Drag:
                    if (!_painting) return;
                    if (x < 0 || x >= p.WidthPx || y < 0 || y >= p.HeightPx) return;
                    if (_pendingPoints[^1] != (x, y))
                    {
                        AppendFreehandTo(x, y);
                        Redraw();
                    }
                    break;

                case PaintEventKind.Up:
                    if (!_painting) return;
                    _painting = false;
                    // A Patch can be a single dabbed pixel; every other
                    // junction type needs an actual drag (>=2 points) - a
                    // stray click with no movement is silently discarded
                    // rather than popping up the "need two points" dialog.
                    if (_junction == RiverJunction.Patch ? _pendingPoints.Count >= 1 : _pendingPoints.Count >= 2)
                        FinishSegment();
                    else
                        CancelPending();
                    break;
            }
            return;
        }

        // -- waypoints mode: click out each point by hand, then "Finish river" --
        if (kind != PaintEventKind.Down) return;
        if (x < 0 || x >= p.WidthPx || y < 0 || y >= p.HeightPx) return;
        if (_pendingPoints.Count == 0 && !ValidateJunctionStart(x, y)) return;

        _pendingPoints.Add((x, y));
        Redraw();
    }

    private void FinishSegment()
    {
        // A patch can be a single dabbed pixel; every other junction type
        // still needs at least two points to form a line.
        if (_junction != RiverJunction.Patch && _pendingPoints.Count < 2)
        {
            Dialogs.Info(_app, Loc.T("rivers.title"), Loc.T("rivers.needTwoPoints"));
            return;
        }
        if (_pendingPoints.Count == 0) return;
        _app.Undo?.SnapshotBeforeChange();
        _riversJustGenerated = false;
        var seg = new RiverSegment { Points = new List<(int, int)>(_pendingPoints), Size = (int)Math.Round(_size), Junction = _junction };
        _app.Project!.RiverSegments.Add(seg);
        _pendingPoints.Clear();
        Redraw();
    }

    private void CancelPending()
    {
        _pendingPoints.Clear();
        _painting = false; // in case this fires (Cancel button, or a mode-switch radio) while a paint-mode drag is still physically in progress
        Redraw();
    }

    private static readonly (int dx, int dy)[] FourDirs = { (1, 0), (-1, 0), (0, 1), (0, -1) };

    /// <summary>Extends `_pendingPoints` from its current last point to
    /// (x, y), bridging any diagonal step to keep every consecutive pair
    /// 4-connected (a fast drag can report two positions several pixels
    /// apart between mouse-move events), and collapsing the stroke the
    /// moment it becomes 4-adjacent to an earlier, non-consecutive point
    /// already recorded - the user explicitly asked for a freehand line
    /// that "always stays connected and never describes circles/pixel
    /// clusters" when doubling back on itself. Safe to apply here (unlike
    /// the old dense full-resolution D8 trace, where the equivalent check was
    /// tried and reverted - see CLAUDE.md Runde 23/24):
    /// a human drawing a river essentially never NEEDS to pass within one
    /// pixel of an earlier part of the same stroke, so collapsing that is
    /// exactly the wanted behavior rather than a destructive false
    /// positive on ordinary curve geometry.</summary>
    private void AppendFreehandTo(int x, int y)
    {
        var (lastX, lastY) = _pendingPoints[^1];
        int dx = Math.Abs(x - lastX), dy = -Math.Abs(y - lastY);
        int sx = lastX < x ? 1 : -1, sy = lastY < y ? 1 : -1;
        int err = dx + dy;
        int cx = lastX, cy = lastY;
        bool first = true;
        while (true)
        {
            if (!first)
            {
                var (px, py) = _pendingPoints[^1];
                if (px != cx && py != cy) CollapseAppend((cx, py)); // bridge pixel, shares an edge with both neighbors
                CollapseAppend((cx, cy));
            }
            first = false;
            if (cx == x && cy == y) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; cx += sx; }
            if (e2 <= dx) { err += dx; cy += sy; }
        }
    }

    /// <summary>Appends `pt` unless it exactly repeats an earlier point
    /// already in `_pendingPoints`, in which case everything after that
    /// earlier point is dropped instead, collapsing the loop to its first
    /// visit and pt is NOT re-added (it is already represented there). If
    /// `pt` merely becomes 4-adjacent to an earlier, non-consecutive point
    /// (other than the immediate last point, which is always adjacent to
    /// the next one by construction), the same collapse happens but `pt`
    /// - being a genuinely new, distinct point - IS still added right
    /// after it, since it connects cleanly to what remains. (A prior
    /// version treated both cases the same and never added `pt` in the
    /// adjacency case either, silently dropping it - the exact cause of a
    /// real 2px gap found live: a hand-drawn stroke's natural tremor
    /// brought it 4-adjacent to an earlier point of the same stroke, and
    /// the very next pixel vanished instead of connecting.)</summary>
    private void CollapseAppend((int x, int y) pt)
    {
        int lastIdx = _pendingPoints.Count - 1;
        for (int i = 0; i <= lastIdx; i++)
        {
            if (_pendingPoints[i] == pt)
            {
                _pendingPoints.RemoveRange(i + 1, _pendingPoints.Count - i - 1);
                return;
            }
            if (i != lastIdx)
            {
                foreach (var (fx, fy) in FourDirs)
                {
                    if (_pendingPoints[i] == (pt.x + fx, pt.y + fy))
                    {
                        _pendingPoints.RemoveRange(i + 1, _pendingPoints.Count - i - 1);
                        _pendingPoints.Add(pt);
                        return;
                    }
                }
            }
        }
        _pendingPoints.Add(pt);
    }

    private void DeleteSelected()
    {
        int idx = _segList.SelectedIndex;
        if (idx < 0) return;
        _app.Undo?.SnapshotBeforeChange();
        _riversJustGenerated = false;
        _app.Project!.RiverSegments.RemoveAt(idx);
        _segList.SelectedIndex = -1;
        Redraw();
    }

    private void ClearAll()
    {
        if (_app.Project!.RiverSegments.Count == 0) return;
        if (Dialogs.Confirm(_app, "Rivers", "Remove all river segments?"))
        {
            _app.Undo?.SnapshotBeforeChange();
            _app.Project.RiverSegments.Clear();
            _segList.SelectedIndex = -1;
            Redraw();
        }
    }

    private void RegenerateRivers(bool newSeed)
    {
        var p = _app.Project!;
        if (p.RiverSegments.Count > 0 && !_riversJustGenerated && !Dialogs.Confirm(_app, "Rivers", "Replace all existing river segments with a freshly auto-generated network? (Ctrl+Z undoes this.)"))
            return;
        _app.Undo?.SnapshotBeforeChange();
        // New seed: river-only RNG, the tile's own seed (Settings.RandomSeed) stays untouched.
        var rng = newSeed ? new Random(Random.Shared.Next())
            : p.Settings.RandomSeed.HasValue ? new Random(p.Settings.RandomSeed.Value) : new Random();
        _segList.SelectedIndex = -1;
        _riversJustGenerated = true;
        var height = p.Height ?? p.EnsureHeight();
        p.RiverSegments.Clear();
        p.RiverSegments.AddRange(RiverMapGen.AutoGenerateRivers(p.LandMask, height, (int)Math.Round(_autoRiverCount), rng, _autoTributaryFrequency, _autoDistributaryFrequency, _autoMinRiverLength, (int)Math.Round(_autoRiverSmoothness), p.ImportedRiverRaster));
        Redraw();
    }

    private void DiscardImported()
    {
        if (Dialogs.Confirm(_app, "Rivers", "Discard the river background loaded from the existing tile? This cannot be undone (save your project first if unsure)."))
        {
            _app.Undo?.SnapshotBeforeChange();
            _app.Project!.ImportedRiverRaster = null;
            OnShow();
        }
    }
}
