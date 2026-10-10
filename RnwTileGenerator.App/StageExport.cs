using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Export stage: validate, preview, and write the four game
/// files. Port of gui/stage_export.py.</summary>
public sealed class StageExport : IStage
{
    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _validationLabel;
    private readonly TextBlock _exportLabel;
    private readonly TextBox _outDirBox;
    private readonly TextBlock _tileNameLabel;

    private string _previewMode = "provinces"; // "provinces" | "height" | "rivers"

    /// <summary>Remembered target (layout + one folder per layout), see ExportPrefs.</summary>
    private readonly ExportPrefs _prefs = ExportPrefs.Load();
    private ExportLayout _layout;
    private bool _switchingLayout;

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    public IReadOnlyList<ToolbarAction> ToolbarActions =>
    [
        new(Loc.T("toolbar.export.validate"), null, Validate),
    ];

    public StageExport(MainWindow app)
    {
        _app = app;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold("Export"));

        var nameRow = Ui.Stack();
        _tileNameLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) };
        nameRow.Children.Add(_tileNameLabel);
        nameRow.Children.Add(Ui.Button(Loc.T("export.renameTile"), (_, _) => RenameTile()));
        side.Children.Add(Ui.Group(Loc.T("export.tileNameGroup"), nameRow));

        var pf = Ui.Stack();
        pf.Children.Add(Ui.Radio("Province map", "export-preview", true, (_, _) => { _previewMode = "provinces"; Redraw(); }));
        pf.Children.Add(Ui.Radio("Height map", "export-preview", false, (_, _) => { _previewMode = "height"; Redraw(); }));
        pf.Children.Add(Ui.Radio("River map", "export-preview", false, (_, _) => { _previewMode = "rivers"; Redraw(); }));
        side.Children.Add(Ui.Group("Preview", pf));

        side.Children.Add(Ui.Button("Run validation checks", (_, _) => Validate()));
        _validationLabel = Ui.Info(260);
        side.Children.Add(_validationLabel);

        side.Children.Add(Ui.Sep());

        _layout = _prefs.Layout;
        var layoutPanel = Ui.Stack();
        var tileFolderRadio = Ui.Radio(Loc.T("export.layout.tileFolder"), "export-layout", _layout == ExportLayout.TileFolder, (_, _) => SwitchLayout(ExportLayout.TileFolder));
        tileFolderRadio.ToolTip = Loc.T("export.layout.tileFolder.tip");
        layoutPanel.Children.Add(tileFolderRadio);
        var modFolderRadio = Ui.Radio(Loc.T("export.layout.modFolder"), "export-layout", _layout == ExportLayout.ModFolder, (_, _) => SwitchLayout(ExportLayout.ModFolder));
        modFolderRadio.ToolTip = Loc.T("export.layout.modFolder.tip");
        layoutPanel.Children.Add(modFolderRadio);
        side.Children.Add(Ui.Group(Loc.T("export.layout.group"), layoutPanel));

        side.Children.Add(new TextBlock { Text = "Output folder:", Margin = new Thickness(0, 0, 0, 2) });
        _outDirBox = new TextBox { Text = PathForLayout(_layout) };
        // Remember every edit (typed or picked via Browse) for the current layout.
        _outDirBox.TextChanged += (_, _) =>
        {
            if (_switchingLayout) return;
            _prefs.SetPath(_layout, _outDirBox.Text.Trim());
            _prefs.Save();
        };
        side.Children.Add(_outDirBox);
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        folderRow.Children.Add(Ui.Button("Browse...", (_, _) => Browse()));
        var openFolderBtn = Ui.Button("\U0001F4C2", (_, _) => OpenOutputFolder());
        openFolderBtn.ToolTip = Loc.T("export.openOutputFolder");
        openFolderBtn.Margin = new Thickness(4, 0, 0, 0);
        folderRow.Children.Add(openFolderBtn);
        side.Children.Add(folderRow);

        side.Children.Add(Ui.Button("Export tile files", (_, _) => Export()));
        _exportLabel = Ui.Info(260);
        side.Children.Add(_exportLabel);

        _canvas = new PaintCanvasControl { ImageProvider = ProvideImage };

        View = Ui.SideAndCanvas(Ui.Sidebar(280, side), _canvas);
    }

    private byte[] ProvideImage(Int32Rect rect) => _previewMode switch
    {
        "height" => Render.CompositeHeight(_app.Project!, rect),
        "rivers" => Render.CompositeRivers(_app.Project!, rect, false),
        _ => Render.CompositeProvinces(_app.Project!, rect, false, null),
    };

    private void Redraw() => _canvas.RequestRedraw();

    public void OnShow()
    {
        var p = _app.Project!;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        UpdateTileNameLabel();
        Redraw();
    }

    private void UpdateTileNameLabel() => _tileNameLabel.Text = string.Format(Loc.T("export.currentTileName"), _app.Project!.Name);

    private void RenameTile()
    {
        var p = _app.Project!;
        var result = Dialogs.OneText(_app, Loc.T("export.renameTile"), Loc.T("export.renameTile.label"), p.Name);
        if (result == null) return; // cancelled
        result = result.Trim();
        if (result.Length == 0) return; // keep the existing name rather than silently blanking it
        _app.Undo?.SnapshotBeforeChange();
        p.Name = result;
        UpdateTileNameLabel();
        _app.RefreshStatus();
    }

    private string PathForLayout(ExportLayout layout) =>
        _prefs.PathFor(layout) ?? (layout == ExportLayout.TileFolder ? _app.DefaultExportDir() : "");

    /// <summary>Each layout keeps its own folder: switching shows the one last used for it.</summary>
    private void SwitchLayout(ExportLayout layout)
    {
        if (layout == _layout) return;
        _layout = layout;
        _prefs.Layout = layout;
        _switchingLayout = true;
        _outDirBox.Text = PathForLayout(layout);
        _switchingLayout = false;
        _prefs.Save();
    }

    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = _layout == ExportLayout.ModFolder ? Loc.T("export.layout.pickMod") : "Choose output folder" };
        string current = _outDirBox.Text.Trim();
        if (current.Length > 0 && Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(_app) == true) _outDirBox.Text = dlg.FolderName;
    }

    /// <summary>Opens the output folder textbox's path in Windows Explorer
    /// (Nutzer-Wunsch, Runde 23) - a plain error dialog rather than
    /// silently creating the folder if it does not exist yet, since
    /// nothing has necessarily been exported there.</summary>
    private void OpenOutputFolder()
    {
        string dir = _outDirBox.Text.Trim();
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            Dialogs.Error(_app, "Export", $"Folder does not exist yet:\n{dir}");
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    /// <summary>null when the river layer is fine; otherwise the localized crash warning
    /// with up to 5 coordinates.</summary>
    private static string? RiverCycleWarning(TileProject p)
    {
        var rep = p.CheckRiverGraph();
        if (rep.IsAcyclic) return null;
        string coords = string.Join("; ", rep.Offenders.Select(o => $"({o.x}, {o.y})"));
        return string.Format(Loc.T("export.riverCycleWarning"), coords, rep.ExcessEdges + rep.Degree4Pixels);
    }

    private void Validate()
    {
        var p = _app.Project!;
        var issues = new List<string>();
        if (!p.LandMask.AnyTrue())
            issues.Add("No land has been painted (coastline stage) - the whole tile is water.");
        if (p.Height == null)
            issues.Add("Height map has not been generated yet (Mountains & Height stage).");
        if (p.ProvinceResult == null)
            issues.Add("Provinces have not been generated yet (Provinces stage).");
        else
        {
            int n = p.TotalProvinceCount();
            issues.Add($"{n} total provinces.");
            var warn = p.ProvinceLimitWarning();
            if (warn != null) issues.Add($"WARNING: {warn}");
            var counts = p.ProvinceResult.Counts();
            bool anyWater = !p.LandMask.AllTrue();
            bool anyLand = p.LandMask.AnyTrue();
            if (counts.GetValueOrDefault(ProvinceKind.Sea) == 0 && anyWater)
                issues.Add("No sea provinces were generated even though there is water on the tile.");
            if (counts.GetValueOrDefault(ProvinceKind.Land) == 0 && anyLand)
                issues.Add("No land provinces were generated even though there is land on the tile.");
        }
        var riverWarn = RiverCycleWarning(p);
        if (riverWarn != null) issues.Add($"WARNING: {riverWarn}");
        if (!(p.GridW >= 1 && p.GridW <= Constants.MaxTileGridW && p.GridH >= 1 && p.GridH <= Constants.MaxTileGridH))
            issues.Add("Tile size is out of the legal grid range.");
        _validationLabel.Text = issues.Count > 0 ? string.Join("\n", issues) : "No problems found.";
    }

    /// <summary>Export with the settings of this tab - also used by the toolbar's Export button.</summary>
    public void ExportNow() => Export();

    private void Export()
    {
        var p = _app.Project!;
        if (p.ProvinceResult == null)
        {
            Dialogs.Error(_app, "Export", "Generate or draw provinces before exporting.");
            return;
        }
        string dir = _outDirBox.Text.Trim();
        if (dir.Length == 0)
        {
            Dialogs.Error(_app, "Export", Loc.T("export.noFolder"));
            return;
        }
        // A mod folder must already exist - a typo should not silently create a stray mod.
        if (_layout == ExportLayout.ModFolder && !Directory.Exists(dir))
        {
            Dialogs.Error(_app, "Export", string.Format(Loc.T("export.layout.modMissing"), dir));
            return;
        }
        var exportRiverWarn = RiverCycleWarning(p);
        if (exportRiverWarn != null && !Dialogs.Confirm(_app, "Export", exportRiverWarn + "\n\n" + Loc.T("export.exportAnyway")))
            return;
        List<string> paths;
        try
        {
            paths = p.Export(dir, _layout);
        }
        catch (Exception exc)
        {
            Dialogs.Error(_app, "Export failed", exc.Message);
            return;
        }
        string tileDir = TileProject.ResolveExportPaths(dir, p.Name, _layout).TileDir;
        _exportLabel.Text = $"Wrote to {tileDir}:\n" + string.Join("\n", paths.Select(path => Path.GetRelativePath(tileDir, path)));
        Dialogs.Info(_app, "Export", string.Format(Loc.T("export.done"), tileDir, p.Name));
    }
}
