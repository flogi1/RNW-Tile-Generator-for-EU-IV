using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Provinces stage: automatic Voronoi-style generation, or a
/// manual draw-the-borders-yourself workflow. Port of
/// gui/stage_provinces.py.</summary>
public sealed class StageProvinces : IStage
{
    private static readonly string[] KindChoices = { "land", "sea", "lake", "wasteland" };

    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _info;
    private readonly StackPanel _autoFrame;
    private readonly StackPanel _manualFrame;
    private readonly StackPanel _rectFrame;

    private readonly Slider _landDensity, _wasteDensity, _seaDensity, _landObscurity, _borderCurviness, _landSizeVariance;
    private readonly CheckBox _wastelandByHeight, _seaSizeGradient, _experimentalEmptyFarSea;
    private readonly Slider _wastelandHeightThreshold;
    private readonly Slider _seaCoastalMultiplier, _seaOceanMultiplier;
    private readonly BrushSizePanel _penBrush;
    private readonly TextBox _rectCountBox;
    private readonly ComboBox _reclassKind;
    private readonly RadioButton _paintToolRadio;
    private readonly TextBlock _pickedColorInfo;

    private int? _hoverPid;
    private string _mode = "auto";       // "auto" | "manual" | "rect"
    private string _manualTool = "pen";  // "pen" | "eraser" | "paint" | "eyedropper"
    private string _clickTool = "none";  // "none" | "reclassify" | "region"
    private bool _showGrid = true;
    private bool _showRiverOverlay = false;
    private bool _showMountainOverlay = false;
    /// <summary>Province last sampled with the eyedropper tool (Runde 7) -
    /// its exact color/id is what the "paint with picked color" brush
    /// applies. Kept as a live ProvinceInfo reference (not just a copied
    /// Rgb) so painting always targets the correct id even if Recolor()
    /// reassigns colors afterward.</summary>
    private ProvinceInfo? _pickedProvince;

    private (double x, double y)? _rectStart;
    private (double x, double y)? _rectEnd;

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    public StageProvinces(MainWindow app)
    {
        _app = app;
        var s = app.Project!.Settings;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold(Loc.T("provinces.title")));

        var mf = Ui.Stack();
        mf.Children.Add(Ui.Radio(Loc.T("provinces.mode.auto"), "provinces-mode", true, (_, _) => { _mode = "auto"; RefreshMode(); }));
        mf.Children.Add(Ui.Radio(Loc.T("provinces.mode.manual"), "provinces-mode", false, (_, _) => { _mode = "manual"; RefreshMode(); }));
        mf.Children.Add(Ui.Radio(Loc.T("provinces.mode.rect"), "provinces-mode", false, (_, _) => { _mode = "rect"; RefreshMode(); }));
        side.Children.Add(Ui.Group("Mode", mf));

        var overlayPanel = Ui.Stack();
        overlayPanel.Children.Add(Ui.CheckBoxCtl(Loc.T("provinces.overlay.rivers"), _showRiverOverlay, (sender, _) => { _showRiverOverlay = ((CheckBox)sender).IsChecked ?? false; Redraw(); }));
        overlayPanel.Children.Add(Ui.CheckBoxCtl(Loc.T("provinces.overlay.mountains"), _showMountainOverlay, (sender, _) => { _showMountainOverlay = ((CheckBox)sender).IsChecked ?? false; Redraw(); }));
        side.Children.Add(Ui.Group(Loc.T("provinces.overlay.group"), overlayPanel));

        _autoFrame = Ui.Stack();
        _landDensity = Ui.LabeledSlider(_autoFrame, "Land provinces / 128x128 cell", 0.5, 30, s.LandDensityPerCell, null,
            "Wie viele Landprovinzen im Schnitt in eine 128x128px Rasterzelle passen. Weiter rechts = mehr, kleinere Provinzen.");
        _wasteDensity = Ui.LabeledSlider(_autoFrame, "Wasteland provinces / 128x128 cell", 0.5, 30, s.WastelandDensityPerCell, null,
            "Wie viele Ödland-Provinzen im Schnitt in eine 128x128px Rasterzelle passen. Weiter rechts = mehr, kleinere Provinzen.");
        double cellArea0 = (double)Constants.GridUnit * Constants.GridUnit;
        double seaDensityInitial = Math.Clamp(cellArea0 / Math.Max(s.SeaSpacing * s.SeaSpacing, 1), 0.1, 10);
        _seaDensity = Ui.LabeledSlider(_autoFrame, "Sea provinces / 128x128 cell", 0.1, 10, seaDensityInitial, null,
            "Wie viele Seeprovinzen im Schnitt in eine 128x128px Rasterzelle passen. Genau wie bei Land/Ödland: weiter rechts = mehr, kleinere Seeprovinzen.");
        _landObscurity = Ui.LabeledSlider(_autoFrame, "Obskurität der Landgrenzen (1-10)", 1, 10, s.LandObscurity, null,
            "Wie unregelmäßig automatisch generierte Landprovinz-Grenzen ausfallen: niedrig = regelmäßiger, an einem Raster orientiert; hoch = unruhiger und weniger vorhersehbar. " +
            "Unabhängig davon versuchen Grenzen bei jeder Stufe, Flüssen und Gebirgsketten zu folgen statt sie zu durchschneiden - ein erster, heuristischer Ansatz, keine echte Geographie-Simulation.");
        _borderCurviness = Ui.LabeledSlider(_autoFrame, Loc.T("provinces.borderCurviness"), 0, 1, s.BorderCurviness, null, Loc.T("provinces.borderCurviness.tip"));
        _landSizeVariance = Ui.LabeledSlider(_autoFrame, Loc.T("provinces.landSizeVariance"), 0, 1, s.LandSizeVariance, null, Loc.T("provinces.landSizeVariance.tip"));

        var wasteHeightPanel = Ui.Stack();
        _wastelandByHeight = Ui.CheckBoxCtl(Loc.T("provinces.wastelandByHeight"), s.WastelandByHeightEnabled, (sender, _) =>
        {
            bool on = ((CheckBox)sender).IsChecked ?? false;
            // _wastelandHeightThreshold is only actually assigned a few
            // lines below (it needs the checkbox above it to already exist
            // as this panel's first child) - by the time a user can
            // actually toggle this checkbox, construction has long since
            // finished and the field is set, but the compiler's nullable
            // flow analysis can't see that far ahead, hence the
            // null-forgiving `!` here rather than a dereference warning.
            _wastelandHeightThreshold!.IsEnabled = on;
        });
        _wastelandByHeight.ToolTip = Loc.T("provinces.wastelandByHeight.tip");
        wasteHeightPanel.Children.Add(_wastelandByHeight);
        _wastelandHeightThreshold = Ui.LabeledSliderWithValue(wasteHeightPanel, Loc.T("provinces.wastelandHeightThreshold"), 96, 235, s.WastelandHeightThreshold, null, Loc.T("provinces.wastelandHeightThreshold.tip"));
        _wastelandHeightThreshold.IsEnabled = s.WastelandByHeightEnabled;
        _autoFrame.Children.Add(Ui.Group(Loc.T("provinces.wastelandByHeightGroup"), wasteHeightPanel));

        var seaPanel = Ui.Stack();
        _seaSizeGradient = Ui.CheckBoxCtl(Loc.T("provinces.seaSizeGradient"), s.SeaSizeGradient, (_, _) => { });
        _seaSizeGradient.ToolTip = Loc.T("provinces.seaSizeGradient.tip");
        seaPanel.Children.Add(_seaSizeGradient);
        // Direct "N x an average province" framing per Runde 7 feedback,
        // rather than more internal tuning of a fixed heuristic - the user
        // asked specifically for a slider here, split coast/ocean.
        _seaCoastalMultiplier = Ui.LabeledSlider(seaPanel, Loc.T("provinces.seaCoastalMultiplier"), 1, 15, s.SeaCoastalAreaMultiplier, null, Loc.T("provinces.seaCoastalMultiplier.tip"));
        _seaOceanMultiplier = Ui.LabeledSlider(seaPanel, Loc.T("provinces.seaOceanMultiplier"), 5, 60, s.SeaOceanAreaMultiplier, null, Loc.T("provinces.seaOceanMultiplier.tip"));
        _experimentalEmptyFarSea = Ui.CheckBoxCtl(Loc.T("provinces.experimentalEmptyFarSea"), s.ExperimentalEmptyFarSea, (_, _) => { });
        _experimentalEmptyFarSea.ToolTip = Loc.T("provinces.experimentalEmptyFarSea.tip");
        seaPanel.Children.Add(_experimentalEmptyFarSea);
        _autoFrame.Children.Add(Ui.Group(Loc.T("provinces.seaGroup"), seaPanel));

        _autoFrame.Children.Add(Ui.Button(Loc.T("provinces.generate"), (_, _) => GenerateAuto()));
        // Runde 7, dritte Rückmeldung: two coastline-preserving
        // regeneration modes - re-roll just the land/wasteland cutting or
        // just the sea/lake cutting, leaving the other domain (and the
        // coastline itself, since LandMask is never touched by either)
        // completely untouched. Useful once the coastline looks good and
        // only the province layout on one side of it should change.
        var landOnlyBtn = Ui.Button(Loc.T("provinces.generateLandOnly"), (_, _) => RegenerateLandOnly());
        landOnlyBtn.ToolTip = Loc.T("provinces.generateLandOnly.tip");
        _autoFrame.Children.Add(landOnlyBtn);
        var seaOnlyBtn = Ui.Button(Loc.T("provinces.generateSeaOnly"), (_, _) => RegenerateSeaOnly());
        seaOnlyBtn.ToolTip = Loc.T("provinces.generateSeaOnly.tip");
        _autoFrame.Children.Add(seaOnlyBtn);
        side.Children.Add(_autoFrame);

        _manualFrame = Ui.Stack();
        _manualFrame.Children.Add(Ui.Wrap(
            "1) Grenzen mit dem Stift einzeichnen (Provinzen müssen komplett umschlossen sein).\n" +
            "2) Unten auf \"Grenzen einfärben\" klicken - das füllt jede umschlossene Fläche mit einer eigenen Provinzfarbe und die violette Grenzlinie selbst verschwindet dabei vollständig (auch in der exportierten Datei bleibt davon nichts sichtbar).\n" +
            "3) Nach jeder Änderung an den Grenzen erneut klicken, um die Farben zu aktualisieren.\n\n" +
            "Alternativ, um eine bestehende Provinz direkt zu bearbeiten: mit der Pipette auf sie klicken (übernimmt ihre exakte Farbe, unten als RGB angezeigt), dann mit \"Mit gewählter Farbe malen\" ihr Gebiet erweitern. Das funktioniert nur innerhalb derselben Art (Land/See/See-Loch/Ödland), damit z.B. Land nicht versehentlich zu Wasser umdeklariert wird.", 250));
        _manualFrame.Children.Add(Ui.Radio(Loc.T("provinces.tool.pen"), "provinces-manual-tool", true, (_, _) => _manualTool = "pen"));
        _manualFrame.Children.Add(Ui.Radio(Loc.T("provinces.tool.eraser"), "provinces-manual-tool", false, (_, _) => _manualTool = "eraser"));
        _paintToolRadio = Ui.Radio(Loc.T("provinces.tool.paint"), "provinces-manual-tool", false, (_, _) => _manualTool = "paint");
        _paintToolRadio.ToolTip = Loc.T("provinces.tool.paint.tip");
        _manualFrame.Children.Add(_paintToolRadio);
        var eyedropperRadio = Ui.Radio(Loc.T("provinces.tool.eyedropper"), "provinces-manual-tool", false, (_, _) => _manualTool = "eyedropper");
        eyedropperRadio.ToolTip = Loc.T("provinces.tool.eyedropper.tip");
        _manualFrame.Children.Add(eyedropperRadio);
        _pickedColorInfo = Ui.Info(250);
        _pickedColorInfo.Text = Loc.T("provinces.tool.noColorPicked");
        _manualFrame.Children.Add(_pickedColorInfo);
        _penBrush = new BrushSizePanel("Pen size (px)", 0, 40, 2, "provinces-pen",
            new double[] { 1, 2, 5, 10 },
            "Wie breit der Stift beim Einzeichnen/Löschen von Provinzgrenzen ist - gilt auch für den \"Mit gewählter Farbe malen\"-Pinsel.");
        _manualFrame.Children.Add(_penBrush.View);
        _manualFrame.Children.Add(Ui.Button(Loc.T("provinces.rebuild"), (_, _) => RebuildManual()));
        _manualFrame.Children.Add(Ui.Button("↶ " + Loc.T("common.undo") + " (Ctrl+Z)", (_, _) => _app.Undo?.Undo()));
        side.Children.Add(_manualFrame);

        _rectFrame = Ui.Stack();
        _rectFrame.Children.Add(Ui.Wrap(
            "Mit der Maus ein Rechteck auf der Karte aufziehen (klicken und ziehen), Zielanzahl eintragen und generieren. " +
            "Nur reines Landgebiet innerhalb des Rechtecks wird ersetzt - Meer, Seen, Ödland und alles außerhalb des Rechtecks bleiben unverändert, ebenso die Farben der übrigen Provinzen.", 250));
        _rectFrame.Children.Add(new TextBlock { Text = "Zielanzahl Landprovinzen im Bereich", Margin = new Thickness(0, 6, 0, 2) });
        _rectCountBox = new TextBox { Text = "20", ToolTip = "Ungefähre Zielanzahl - wird wie beim automatischen Modus über die Provinzdichte angenähert, nicht exakt getroffen." };
        _rectFrame.Children.Add(_rectCountBox);
        _rectFrame.Children.Add(Ui.Button("Provinzen im Rechteck generieren", (_, _) => GenerateRect()));
        _rectFrame.Children.Add(Ui.Button("Auswahl löschen", (_, _) => { _rectStart = null; _rectEnd = null; Redraw(); }));
        side.Children.Add(_rectFrame);

        side.Children.Add(Ui.Sep());

        var rf = Ui.Stack();
        rf.Children.Add(Ui.Radio("Just inspect (no click action)", "provinces-click", true, (_, _) => _clickTool = "none"));
        rf.Children.Add(Ui.Radio("Reclassify to:", "provinces-click", false, (_, _) => _clickTool = "reclassify"));
        _reclassKind = new ComboBox { ItemsSource = KindChoices, SelectedItem = "wasteland", Margin = new Thickness(20, 0, 0, 4), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        rf.Children.Add(_reclassKind);
        rf.Children.Add(Ui.Radio("Add as region seed", "provinces-click", false, (_, _) => _clickTool = "region"));
        side.Children.Add(Ui.Group("Reclassify / regions (click a province)", rf));

        side.Children.Add(Ui.Button("Recolor all provinces", (_, _) => Recolor()));

        side.Children.Add(Ui.CheckBoxCtl(Loc.T("provinces.showGrid"), _showGrid, (sender, _) =>
        {
            _showGrid = ((CheckBox)sender).IsChecked ?? false;
            Redraw();
        }));

        _info = Ui.Info(250);
        side.Children.Add(_info);

        _canvas = new PaintCanvasControl { ImageProvider = ProvideImage };
        _canvas.OnPaint = OnPaint;
        _penBrush.Changed += UpdateBrushPreview;

        View = Ui.SideAndCanvas(Ui.Sidebar(270, side), _canvas);
        RefreshMode();
    }

    private void UpdateBrushPreview() =>
        _canvas.BrushPreviewRadius = _mode == "manual" ? _penBrush.EffectiveRadius : null;

    // -- rendering --------------------------------------------------------------

    private byte[] ProvideImage(Int32Rect rect)
    {
        var p = _app.Project!;
        var buf = Render.CompositeProvinces(p, rect, _showGrid, _hoverPid);

        // Terrain overlay: subtle, non-intrusive tints (not a hard
        // overwrite) so rivers/mountain ridges stay visible as a guide for
        // drawing realistic province borders without hiding the province
        // colors underneath them.
        if (_showRiverOverlay || _showMountainOverlay)
        {
            int ow = rect.Width, oh = rect.Height, fullW = p.LandMask.Width;
            byte[]? riverIdx = _showRiverOverlay ? p.RiverRaster() : null;
            for (int y = 0; y < oh; y++)
            {
                int srcRow = (rect.Y + y) * fullW;
                for (int x = 0; x < ow; x++)
                {
                    int srcIdx = srcRow + rect.X + x;
                    int o = (y * ow + x) * 3;
                    if (_showMountainOverlay)
                    {
                        double m = p.MountainMask.Data[srcIdx] / 255.0;
                        if (m > 0.12)
                        {
                            double a = Math.Min(m, 1.0) * 0.4;
                            buf[o] = (byte)Math.Clamp(buf[o] * (1 - a) + 200 * a, 0, 255);
                            buf[o + 1] = (byte)Math.Clamp(buf[o + 1] * (1 - a) + 60 * a, 0, 255);
                            buf[o + 2] = (byte)Math.Clamp(buf[o + 2] * (1 - a) + 30 * a, 0, 255);
                        }
                    }
                    if (riverIdx != null)
                    {
                        byte v = riverIdx[srcIdx];
                        if (v != Constants.RiverPalLandBg && v != Constants.RiverPalSeaBg)
                        {
                            const double a = 0.55;
                            buf[o] = (byte)Math.Clamp(buf[o] * (1 - a) + 40 * a, 0, 255);
                            buf[o + 1] = (byte)Math.Clamp(buf[o + 1] * (1 - a) + 190 * a, 0, 255);
                            buf[o + 2] = (byte)Math.Clamp(buf[o + 2] * (1 - a) + 255 * a, 0, 255);
                        }
                    }
                }
            }
        }

        if (_mode == "manual" && p.ManualProvinceBorderMask != null)
        {
            var borderMask = p.ManualProvinceBorderMask;
            int w = rect.Width, h = rect.Height, fullW = borderMask.Width;
            for (int y = 0; y < h; y++)
            {
                int srcRow = (rect.Y + y) * fullW;
                for (int x = 0; x < w; x++)
                {
                    if (borderMask.Data[srcRow + rect.X + x] >= 128)
                    {
                        int o = (y * w + x) * 3;
                        buf[o] = 255; buf[o + 1] = 0; buf[o + 2] = 255;
                    }
                }
            }
        }
        if (_mode == "rect" && _rectStart.HasValue && _rectEnd.HasValue)
        {
            int rx0 = (int)Math.Min(_rectStart.Value.x, _rectEnd.Value.x);
            int rx1 = (int)Math.Max(_rectStart.Value.x, _rectEnd.Value.x);
            int ry0 = (int)Math.Min(_rectStart.Value.y, _rectEnd.Value.y);
            int ry1 = (int)Math.Max(_rectStart.Value.y, _rectEnd.Value.y);
            DrawRectOutline(buf, rect, rx0, ry0, rx1, ry1, 0, 255, 255);
        }
        return buf;
    }

    /// <summary>Draws a 1px cyan outline of the image-space rectangle
    /// [rx0,ry0]-[rx1,ry1] into `buf` (rect.Width*rect.Height*3 RGB24,
    /// covering the visible crop `rect`), used only as a live selection
    /// preview while the region-regeneration rectangle is being dragged -
    /// never touches the actual province data.</summary>
    private static void DrawRectOutline(byte[] buf, Int32Rect rect, int rx0, int ry0, int rx1, int ry1, byte r, byte g, byte b)
    {
        void SetPixel(int x, int y)
        {
            if (x < rect.X || x >= rect.X + rect.Width || y < rect.Y || y >= rect.Y + rect.Height) return;
            int o = ((y - rect.Y) * rect.Width + (x - rect.X)) * 3;
            buf[o] = r; buf[o + 1] = g; buf[o + 2] = b;
        }
        for (int x = rx0; x <= rx1; x++) { SetPixel(x, ry0); SetPixel(x, ry1); }
        for (int y = ry0; y <= ry1; y++) { SetPixel(rx0, y); SetPixel(rx1, y); }
    }

    private void Redraw()
    {
        _canvas.RequestRedraw();
        UpdateInfo();
    }

    public void OnShow()
    {
        var p = _app.Project!;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        if (p.ManualProvinceBorderMask == null || p.ManualProvinceBorderMask.Width != p.WidthPx || p.ManualProvinceBorderMask.Height != p.HeightPx)
            p.ManualProvinceBorderMask = GrayMap.Bool(p.WidthPx, p.HeightPx, false);
        // A picked province can go stale across a project swap (Undo/Redo,
        // or loading a different tile) - the numeric id could now belong to
        // an unrelated province, or not exist at all. Only keep the pick if
        // it's still literally the same ProvinceInfo object in the current
        // result.
        if (_pickedProvince != null &&
            (p.ProvinceResult == null || !p.ProvinceResult.Provinces.TryGetValue(_pickedProvince.ProvinceId, out var stillThere) || !ReferenceEquals(stillThere, _pickedProvince)))
        {
            _pickedProvince = null;
            _pickedColorInfo.Text = Loc.T("provinces.tool.noColorPicked");
        }
        UpdateBrushPreview();
        Redraw();
    }

    private void RefreshMode()
    {
        _autoFrame.Visibility = _mode == "auto" ? Visibility.Visible : Visibility.Collapsed;
        _manualFrame.Visibility = _mode == "manual" ? Visibility.Visible : Visibility.Collapsed;
        _rectFrame.Visibility = _mode == "rect" ? Visibility.Visible : Visibility.Collapsed;
        UpdateBrushPreview();
        Redraw();
    }

    private void UpdateInfo()
    {
        var p = _app.Project!;
        int n = p.TotalProvinceCount();
        var counts = p.ProvinceResult?.Counts();
        string warn = p.ProvinceLimitWarning() ?? "";
        var parts = new List<string>();
        if (counts != null)
            foreach (var kv in counts) parts.Add($"{kv.Key.ToString().ToLowerInvariant()}: {kv.Value}");
        string text = $"Total provinces: {n}\n" + string.Join(", ", parts);
        if (warn.Length > 0) text += $"\n\nWARNING: {warn}";
        if (_hoverPid.HasValue && p.ProvinceResult != null && p.ProvinceResult.Provinces.TryGetValue(_hoverPid.Value, out var info))
            text += $"\n\nHovering province #{info.ProvinceId}: {info.Kind.ToString().ToLowerInvariant()}, {info.PixelCount}px";
        _info.Text = text;
    }

    // -- automatic generation -------------------------------------------------

    /// <summary>Pushes every "Automatic" panel slider/checkbox value into
    /// Settings, the same sync GenerateAuto always did - factored out so
    /// the two coastline-preserving regeneration buttons below can read
    /// exactly the same settings an ordinary automatic generation would,
    /// via TileProject.BuildProvinceGenOptions.</summary>
    private void SyncSettingsFromControls()
    {
        var s = _app.Project!.Settings;
        s.LandDensityPerCell = _landDensity.Value;
        s.WastelandDensityPerCell = _wasteDensity.Value;
        s.LandObscurity = _landObscurity.Value;
        s.BorderCurviness = _borderCurviness.Value;
        s.LandSizeVariance = _landSizeVariance.Value;
        s.WastelandByHeightEnabled = _wastelandByHeight.IsChecked ?? false;
        s.WastelandHeightThreshold = _wastelandHeightThreshold.Value;
        s.SeaSizeGradient = _seaSizeGradient.IsChecked ?? true;
        s.ExperimentalEmptyFarSea = _experimentalEmptyFarSea.IsChecked ?? false;
        s.SeaCoastalAreaMultiplier = _seaCoastalMultiplier.Value;
        s.SeaOceanAreaMultiplier = _seaOceanMultiplier.Value;
        // The generator itself still works in terms of seed spacing (px) -
        // only the UI is unified to a "density per cell" scale (matching
        // land/wasteland, right = more provinces) for consistency, so we
        // convert here using the same spacing<->density relationship the
        // land/wasteland sliders already use internally.
        double cellArea = (double)Constants.GridUnit * Constants.GridUnit;
        s.SeaSpacing = Math.Max(Math.Sqrt(cellArea / Math.Max(_seaDensity.Value, 0.05)), 20);
    }

    private void GenerateAuto()
    {
        _app.Undo?.SnapshotBeforeChange();
        SyncSettingsFromControls();
        _app.Project!.GenerateProvincesAuto();
        var warn = _app.Project.ProvinceLimitWarning();
        Redraw();
        if (warn != null) Dialogs.Warn(_app, "Province count", warn);
    }

    /// <summary>"Landprovinzen neu generieren" (Runde 7, dritte
    /// Rückmeldung): re-rolls only the land+wasteland province cutting,
    /// keeping the coastline and every sea/lake province untouched.</summary>
    private void RegenerateLandOnly()
    {
        var p = _app.Project!;
        if (p.ProvinceResult == null)
        {
            Dialogs.Warn(_app, "Noch keine Provinzen", "Zuerst im \"Automatic\"- oder \"Manual\"-Modus eine Provinzeinteilung erzeugen, bevor nur die Landprovinzen neu generiert werden können.");
            return;
        }
        _app.Undo?.SnapshotBeforeChange();
        SyncSettingsFromControls();
        try
        {
            p.RegenerateLandProvinces();
        }
        catch (Exception exc)
        {
            Dialogs.Error(_app, "Generierung fehlgeschlagen", exc.Message);
            return;
        }
        var warn = p.ProvinceLimitWarning();
        Redraw();
        if (warn != null) Dialogs.Warn(_app, "Province count", warn);
    }

    /// <summary>"Seeprovinzen neu generieren" (Runde 7, dritte
    /// Rückmeldung): symmetric counterpart to RegenerateLandOnly - keeps
    /// the coastline and every land/wasteland province untouched.</summary>
    private void RegenerateSeaOnly()
    {
        var p = _app.Project!;
        if (p.ProvinceResult == null)
        {
            Dialogs.Warn(_app, "Noch keine Provinzen", "Zuerst im \"Automatic\"- oder \"Manual\"-Modus eine Provinzeinteilung erzeugen, bevor nur die Seeprovinzen neu generiert werden können.");
            return;
        }
        _app.Undo?.SnapshotBeforeChange();
        SyncSettingsFromControls();
        try
        {
            p.RegenerateSeaProvinces();
        }
        catch (Exception exc)
        {
            Dialogs.Error(_app, "Generierung fehlgeschlagen", exc.Message);
            return;
        }
        var warn = p.ProvinceLimitWarning();
        Redraw();
        if (warn != null) Dialogs.Warn(_app, "Province count", warn);
    }

    private void Recolor()
    {
        if (_app.Project!.ProvinceResult == null) return;
        _app.Undo?.SnapshotBeforeChange();
        ProvinceMapGen.Recolor(_app.Project.ProvinceResult, _app.Project.Metadata.EmptyColor);
        Redraw();
    }

    // -- region (rectangle) regeneration ---------------------------------------

    private void GenerateRect()
    {
        var p = _app.Project!;
        if (p.ProvinceResult == null)
        {
            Dialogs.Warn(_app, "Noch keine Provinzen", "Zuerst im \"Automatic\"- oder \"Manual\"-Modus eine Provinzeinteilung erzeugen - der Rechteck-Modus ersetzt nur einen Ausschnitt davon.");
            return;
        }
        if (_rectStart == null || _rectEnd == null)
        {
            Dialogs.Warn(_app, "Kein Bereich ausgewählt", "Zuerst mit der Maus ein Rechteck auf der Karte aufziehen (klicken und ziehen).");
            return;
        }
        if (!int.TryParse(_rectCountBox.Text, out int count) || count < 1)
        {
            Dialogs.Warn(_app, "Ungültige Anzahl", "Bitte eine positive Ganzzahl als Zielanzahl eingeben.");
            return;
        }

        int x0 = (int)_rectStart.Value.x, y0 = (int)_rectStart.Value.y;
        int x1 = (int)_rectEnd.Value.x, y1 = (int)_rectEnd.Value.y;
        _app.Undo?.SnapshotBeforeChange();
        try
        {
            p.GenerateProvincesInRegion(x0, y0, x1, y1, count);
        }
        catch (Exception exc)
        {
            Dialogs.Error(_app, "Generierung fehlgeschlagen", exc.Message);
            return;
        }

        var warn = p.ProvinceLimitWarning();
        Redraw();
        if (warn != null) Dialogs.Warn(_app, "Province count", warn);
    }

    // -- manual drawing -----------------------------------------------------------

    private void RebuildManual()
    {
        var p = _app.Project!;
        _app.Undo?.SnapshotBeforeChange();
        p.ManualProvinceBorderMask ??= GrayMap.Bool(p.WidthPx, p.HeightPx, false);
        var borderMask = p.ManualProvinceBorderMask;

        var labels = new LabelMap(p.WidthPx, p.HeightPx);
        var kindOf = new Dictionary<int, ProvinceKind>();
        int nextId = 0;

        void AddDomain(GrayMap mask, ProvinceKind kind)
        {
            // LabelRegions only ever assigns 1..n to pixels actually
            // reached by its BFS, so every label in that range is
            // guaranteed non-empty - a single O(pixels) remap pass is
            // enough (no need to re-scan the whole image once per region).
            var (regionLabels, n) = RasterOps.LabelRegions(mask, borderMask!);
            if (n == 0) return;
            int baseId = nextId;
            for (int idx = 0; idx < regionLabels.Length; idx++)
            {
                int r = regionLabels[idx];
                if (r > 0) labels.Data[idx] = baseId + r - 1;
            }
            for (int i = 0; i < n; i++) kindOf[baseId + i] = kind;
            nextId += n;
        }

        var plainLand = GrayMap.And(GrayMap.And(p.LandMask, GrayMap.Not(p.WastelandMask)), GrayMap.Not(p.EmptyMask));
        var wasteland = GrayMap.And(GrayMap.And(p.WastelandMask, p.LandMask), GrayMap.Not(p.EmptyMask));
        AddDomain(plainLand, ProvinceKind.Land);
        AddDomain(wasteland, ProvinceKind.Wasteland);

        var waterMask = GrayMap.And(GrayMap.Not(p.LandMask), GrayMap.Not(p.EmptyMask));
        var isSea = ProvinceMapGen.ClassifyWater(waterMask);
        var seaMask = GrayMap.And(waterMask, isSea);
        var lakeMask = GrayMap.And(waterMask, GrayMap.Not(isSea));
        AddDomain(seaMask, ProvinceKind.Sea);
        AddDomain(lakeMask, ProvinceKind.Lake);

        // The border strip itself was deliberately excluded from every
        // domain above (that's what makes it a border), so every pixel
        // under it is still unlabeled at this point. Grow the just-built
        // regions into that strip so the border disappears into ordinary
        // province color instead of staying an uncolored gap.
        RasterOps.FillUnlabeledGaps(labels, p.EmptyMask);

        p.SetManualProvinces(labels, kindOf);
        var warn = p.ProvinceLimitWarning();
        Redraw();
        if (warn != null) Dialogs.Warn(_app, "Province count", warn);
    }

    /// <summary>Eyedropper: samples the exact color of the province under
    /// (ix, iy), remembers it as the "paint with picked color" brush's
    /// target, shows its RGB, and jumps straight to the paint tool so it's
    /// immediately usable (Runde 7 request).</summary>
    private void PickProvinceColor(TileProject p, double ix, double iy)
    {
        if (p.ProvinceResult == null) return;
        int x = (int)Math.Round(ix), y = (int)Math.Round(iy);
        if (x < 0 || x >= p.WidthPx || y < 0 || y >= p.HeightPx) return;
        int pid = p.ProvinceResult.Labels[x, y];
        if (pid < 0 || !p.ProvinceResult.Provinces.TryGetValue(pid, out var info)) return;
        _pickedProvince = info;
        _pickedColorInfo.Text = string.Format(Loc.T("provinces.tool.pickedColor"), info.Color.R, info.Color.G, info.Color.B, info.Kind.ToString().ToLowerInvariant());
        _paintToolRadio.IsChecked = true; // fires its Checked handler -> _manualTool = "paint"
    }

    // -- interaction --------------------------------------------------------------

    private void OnPaint(PaintEventKind kind, double ix, double iy)
    {
        var p = _app.Project!;
        if (kind == PaintEventKind.Hover)
        {
            int x = (int)ix, y = (int)iy;
            int? newHover = null;
            if (p.ProvinceResult != null && x >= 0 && x < p.WidthPx && y >= 0 && y < p.HeightPx)
            {
                int pid = p.ProvinceResult.Labels[x, y];
                newHover = pid >= 0 ? pid : null;
            }
            if (newHover != _hoverPid)
            {
                _hoverPid = newHover;
                Redraw();
            }
            return;
        }

        if (_mode == "manual")
        {
            if (_manualTool == "eyedropper")
            {
                // Single click, not drag - a "pick" is a one-shot action.
                if (kind == PaintEventKind.Down) PickProvinceColor(p, ix, iy);
                return;
            }
            if (_manualTool == "paint")
            {
                if ((kind == PaintEventKind.Down || kind == PaintEventKind.Drag) && p.ProvinceResult != null && _pickedProvince != null)
                {
                    if (kind == PaintEventKind.Down) _app.Undo?.SnapshotBeforeChange();
                    RasterOps.PaintProvinceColor(p.ProvinceResult, ix, iy, _penBrush.EffectiveRadius, _pickedProvince.ProvinceId);
                    Redraw();
                }
                return;
            }
            if (kind == PaintEventKind.Down || kind == PaintEventKind.Drag)
            {
                p.ManualProvinceBorderMask ??= GrayMap.Bool(p.WidthPx, p.HeightPx, false);
                if (kind == PaintEventKind.Down) _app.Undo?.SnapshotBeforeChange();
                bool value = _manualTool == "pen";
                RasterOps.PaintBoolBrush(p.ManualProvinceBorderMask, ix, iy, _penBrush.EffectiveRadius, value);
                Redraw();
            }
            return;
        }

        if (_mode == "rect" && (kind == PaintEventKind.Down || kind == PaintEventKind.Drag || kind == PaintEventKind.Up))
        {
            if (kind == PaintEventKind.Down) _rectStart = (ix, iy);
            if (_rectStart.HasValue) _rectEnd = (ix, iy);
            Redraw();
            return;
        }

        if (kind == PaintEventKind.Down && _clickTool != "none" && p.ProvinceResult != null)
        {
            int x = (int)ix, y = (int)iy;
            if (x < 0 || x >= p.WidthPx || y < 0 || y >= p.HeightPx) return;
            int pid = p.ProvinceResult.Labels[x, y];
            if (pid < 0 || !p.ProvinceResult.Provinces.TryGetValue(pid, out var info)) return;

            _app.Undo?.SnapshotBeforeChange();
            if (_clickTool == "reclassify")
            {
                string choice = (_reclassKind.SelectedItem as string) ?? "wasteland";
                info.Kind = choice switch
                {
                    "sea" => ProvinceKind.Sea,
                    "lake" => ProvinceKind.Lake,
                    "wasteland" => ProvinceKind.Wasteland,
                    _ => ProvinceKind.Land,
                };
            }
            else if (_clickTool == "region")
            {
                var color = info.Color;
                if (!p.Metadata.Regions.Contains(color)) p.Metadata.Regions.Add(color);
            }
            Redraw();
        }
    }
}
