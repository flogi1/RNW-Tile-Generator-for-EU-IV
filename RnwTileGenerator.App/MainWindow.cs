using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RnwTileGenerator.Core;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>A stage tab (Coastline, Mountains, Rivers, Provinces,
/// Metadata, Export). OnShow() mirrors the Python stages' on_show(),
/// called whenever the tab becomes active (rebuild anything that depends
/// on state another tab may have changed).</summary>
public interface IStage
{
    UIElement View { get; }
    void OnShow();

    /// <summary>The stage's zoomable map canvas, if it has one (most
    /// stages do; a couple - e.g. Metadata - don't show a map at all). Lets
    /// the toolbar's "reset view" button reset zoom/pan for whichever stage
    /// is currently active without every stage needing its own copy of
    /// that button. Defaults to null so existing/simple stages don't need
    /// to implement this explicitly.</summary>
    PaintCanvasControl? Canvas => null;
}

/// <summary>
/// Main application window: landing screen, project lifecycle, and the
/// stage tab control. Direct port of the original Python "gui/app.py"
/// App class - built entirely in code (no .xaml), see the project file's
/// "code-only WPF" note.
/// </summary>
public sealed partial class MainWindow : Window
{
    public TileProject? Project { get; private set; }
    public string? CurrentPath { get; private set; }

    /// <summary>Whole-project undo/redo (see UndoManager) - null until a
    /// tile is actually open (the landing screen has nothing to undo).</summary>
    public UndoManager? Undo { get; private set; }

    private readonly List<(string key, string label, IStage stage)> _stages = new();
    private TabControl? _notebook;
    private TextBlock? _status;
    private Button? _undoBtn, _redoBtn;

    /// <summary>Start arguments: update result (see MainWindow.UpdateStart.cs) and the project to open (--open).</summary>
    private readonly UpdateStartInfo _start;

    public MainWindow(UpdateStartInfo start)
    {
        _start = start;
        Title = "RNW Tile Generator";
        Icon = AppIcon.Frame;
        Width = 1280;
        Height = 820;
        Loc.LanguageChanged += OnLanguageChanged;
        BuildLanding();
    }

    private void OnLanguageChanged()
    {
        // Simplest correct way to pick up new-language strings everywhere:
        // rebuild whichever screen is currently showing. Re-entering the
        // editor rebuilds every stage's UI from scratch (a fresh
        // TileProject reference isn't created - Project itself is
        // untouched, only the views wrapping it), so no in-progress
        // paint/generation state is lost, just any not-yet-committed
        // pending clicks (e.g. a half-drawn river polyline).
        if (Project != null) StartEditor(Project, CurrentPath);
        else BuildLanding();
    }

    // -- landing screen -----------------------------------------------------

    private void BuildLanding()
    {
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(new TextBlock
        {
            Text = Loc.T("landing.title"),
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        Button MakeBtn(string text, RoutedEventHandler onClick)
        {
            var b = new Button { Content = text, Width = 300, Height = 32, Margin = new Thickness(0, 4, 0, 4) };
            b.Click += onClick;
            return b;
        }

        box.Children.Add(MakeBtn(Loc.T("landing.newTile"), (_, _) => NewTileFlow()));
        box.Children.Add(MakeBtn(Loc.T("landing.openTile"), (_, _) => OpenExistingTileFlow()));
        box.Children.Add(MakeBtn(Loc.T("landing.openProject"), (_, _) => OpenProjectFlow()));
        box.Children.Add(MakeBtn(Loc.T("landing.importHeightmap"), (_, _) => ImportHeightmapFlow()));
        var traceBtn = MakeBtn(Loc.T("landing.importMapTrace"), (_, _) => ImportMapTraceFlow());
        traceBtn.Foreground = System.Windows.Media.Brushes.DarkOrange; // Runde 9: marked Experimental, see MapTraceImportDialog.cs
        box.Children.Add(traceBtn);

        var langRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        langRow.Children.Add(new TextBlock { Text = "🌐 ", VerticalAlignment = VerticalAlignment.Center });
        // ComboBox shows ToString() by default, which would be the raw
        // enum member name - wrap each language in an explicit
        // ComboBoxItem so the dropdown shows its real display name.
        var langBox = new ComboBox { Width = 160 };
        foreach (var lang in Loc.AllLanguages)
            langBox.Items.Add(new ComboBoxItem { Content = Loc.DisplayName(lang), Tag = lang, IsSelected = lang == Loc.Current });
        langBox.SelectionChanged += (_, _) =>
        {
            if (langBox.SelectedItem is ComboBoxItem { Tag: AppLanguage l }) Loc.SetLanguage(l);
        };
        langRow.Children.Add(langBox);
        box.Children.Add(langRow);

        var root = new Grid();
        root.Children.Add(box);
        // Landing screen: only Language / Help / Donate (File/Edit need an open tile).
        SetScreen(BuildMenu(editor: false), root);
    }

    private void NewTileFlow()
    {
        var dlg = new NewTileDialog(this);
        dlg.ShowDialog();
        if (dlg.Result == null) return;
        var (name, gw, gh) = dlg.Result.Value;
        var project = new TileProject(name, gw, gh);
        StartEditor(project, null);
    }

    private void OpenExistingTileFlow()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Open existing tile .txt file",
            Filter = "RNW tile script (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true) return;
        TileProject project;
        try
        {
            project = TileProject.LoadExistingTile(ofd.FileName);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, "Could not load tile", $"{exc.Message}\n\n{exc}");
            return;
        }
        StartEditor(project, null);
    }

    private void OpenProjectFlow()
    {
        var ofd = new OpenFileDialog
        {
            Title = "Open project",
            Filter = "RNW Tile Generator project (*.rnwproj)|*.rnwproj|All files (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != true) return;
        TileProject project;
        try
        {
            project = TileProject.Load(ofd.FileName);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, "Could not load project", $"{exc.Message}\n\n{exc}");
            return;
        }
        StartEditor(project, ofd.FileName);
    }

    private void ImportHeightmapFlow()
    {
        var dlg = new HeightmapImportDialog(this);
        dlg.ShowDialog();
        if (dlg.Result == null) return;
        var (name, gw, gh, imagePath, options) = dlg.Result.Value;

        var project = new TileProject(name, gw, gh);
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var gray = ImageIO.LoadGrayscaleResized(imagePath, project.WidthPx, project.HeightPx);
            project.ImportHeightmap(gray, options);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, Loc.T("heightmap.title"), $"{exc.Message}\n\n{exc}");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        StartEditor(project, null);
    }

    private void ImportMapTraceFlow()
    {
        var dlg = new MapTraceImportDialog(this);
        dlg.ShowDialog();
        if (dlg.Result == null) return;
        var (name, gw, gh, imagePath, options) = dlg.Result.Value;

        var project = new TileProject(name, gw, gh);
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var gray = ImageIO.LoadGrayscaleResized(imagePath, project.WidthPx, project.HeightPx);
            project.ImportMapTrace(gray, options);
        }
        catch (Exception exc)
        {
            Dialogs.Error(this, Loc.T("mapTrace.title"), $"{exc.Message}\n\n{exc}");
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
        StartEditor(project, null);
    }

    // -- editor ---------------------------------------------------------------

    private void StartEditor(TileProject project, string? path)
    {
        bool isNewProjectInstance = !ReferenceEquals(Project, project);
        Project = project;
        CurrentPath = path;
        if (isNewProjectInstance) Undo = new UndoManager(this);
        // A reference image is loaded/resized for one specific tile's pixel
        // dimensions (see ReferenceOverlay.cs) - carrying it over to a
        // genuinely different tile (new/opened/imported, not just an
        // Undo/Redo swap or a UI language change re-entering the same
        // project) would silently overlay stale, misaligned pixels.
        if (isNewProjectInstance) ReferenceOverlay.Clear();

        var dock = new DockPanel();

        var toolbar = BuildToolbar();
        toolbar.SetValue(DockPanel.DockProperty, Dock.Top);
        dock.Children.Add(toolbar);

        _status = new TextBlock { Text = StatusText(), Margin = new Thickness(6, 3, 6, 3) };
        var statusBar = new Border { BorderBrush = System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(0, 1, 0, 0), Child = _status };
        statusBar.SetValue(DockPanel.DockProperty, Dock.Bottom);
        dock.Children.Add(statusBar);

        _notebook = new TabControl();
        _stages.Clear();
        _stages.Add(("random", Loc.T("stage.random"), new StageRandom(this)));
        _stages.Add(("coastline", Loc.T("stage.coastline"), new StageCoastline(this)));
        _stages.Add(("mountains", Loc.T("stage.mountains"), new StageMountains(this)));
        _stages.Add(("rivers", Loc.T("stage.rivers"), new StageRivers(this)));
        _stages.Add(("provinces", Loc.T("stage.provinces"), new StageProvinces(this)));
        _stages.Add(("specialfeatures", Loc.T("stage.specialFeatures"), new StageSpecialFeatures(this)));
        _stages.Add(("metadata", Loc.T("stage.metadata"), new StageMetadata(this)));
        _stages.Add(("export", Loc.T("stage.export"), new StageExport(this)));

        foreach (var (_, label, stage) in _stages)
            _notebook.Items.Add(new TabItem { Header = label, Content = stage.View });

        _notebook.SelectionChanged += OnTabChanged;
        _notebook.SelectedIndex = 0;
        _stages[0].stage.OnShow();

        dock.Children.Add(_notebook);
        SetScreen(BuildMenu(editor: true), dock);

        PreviewKeyDown -= OnPreviewKeyDown; // avoid a duplicate hook if a second tile is started in the same window
        PreviewKeyDown += OnPreviewKeyDown;

        Undo!.Changed -= UpdateUndoRedoButtons;
        Undo.Changed += UpdateUndoRedoButtons;
        if (isNewProjectInstance) Undo.Reset(); // a brand-new/loaded/imported tile starts with a clean history
        UpdateUndoRedoButtons();
    }

    /// <summary>Swaps in a different TileProject instance (same tile,
    /// different point in its edit history) without tearing down the
    /// editor UI - called only by UndoManager.Undo()/Redo(). Every stage
    /// reads `_app.Project!` dynamically rather than caching it, so
    /// refreshing the active tab's view and the status bar is enough.</summary>
    public void ReplaceProjectInPlace(TileProject newProject)
    {
        Project = newProject;
        if (_notebook != null && _notebook.SelectedIndex >= 0 && _notebook.SelectedIndex < _stages.Count)
            _stages[_notebook.SelectedIndex].stage.OnShow();
        if (_status != null) _status.Text = StatusText();
    }

    /// <summary>Re-reads the status bar text from the current project -
    /// call after changing something StatusText() shows (e.g. the tile
    /// name, Runde 7) without swapping the whole TileProject instance.</summary>
    public void RefreshStatus()
    {
        if (_status != null) _status.Text = StatusText();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A held-down key fires PreviewKeyDown repeatedly (standard OS key
        // repeat) - without this guard, holding Ctrl+Z a beat too long
        // could fire Undo() two or more times for what reads to the user
        // as a single press, silently consuming extra undo history (e.g.
        // wiping out a whole random generation instead of just the one
        // brush stroke intended). Only the first, non-repeat KeyDown of a
        // held chord should ever trigger an action here.
        if (e.IsRepeat) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (!ctrl) return;
        if (e.Key == Key.Z && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            Undo?.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
        {
            Undo?.Redo();
            e.Handled = true;
        }
    }

    private string StatusText()
    {
        var p = Project!;
        return $"  {p.Name}  |  {p.GridW}x{p.GridH} cells  =  {p.WidthPx}x{p.HeightPx} px";
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged is a bubbling routed event - a ComboBox/ListBox
        // selection changing anywhere inside the active tab's content would
        // otherwise also trigger this handler. Only react to the
        // TabControl's own selection actually changing.
        if (!ReferenceEquals(e.OriginalSource, _notebook)) return;
        if (_notebook == null || _notebook.SelectedIndex < 0 || _notebook.SelectedIndex >= _stages.Count) return;
        _stages[_notebook.SelectedIndex].stage.OnShow();
        if (_status != null) _status.Text = StatusText();
    }

    public void GoToExport()
    {
        int idx = _stages.FindIndex(s => s.key == "export");
        if (_notebook != null && idx >= 0) _notebook.SelectedIndex = idx;
    }

    // -- toolbar (undo/redo) ----------------------------------------------------

    private ToolBar BuildToolbar()
    {
        var bar = new ToolBar();
        _undoBtn = new Button { Content = "↶ " + Loc.T("common.undo"), ToolTip = Loc.T("toolbar.undo.tip"), Padding = new Thickness(8, 2, 8, 2) };
        _undoBtn.Click += (_, _) => Undo?.Undo();
        _redoBtn = new Button { Content = "↷ " + Loc.T("common.redo"), ToolTip = Loc.T("toolbar.redo.tip"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(4, 0, 0, 0) };
        _redoBtn.Click += (_, _) => Undo?.Redo();
        bar.Items.Add(_undoBtn);
        bar.Items.Add(_redoBtn);

        // Deliberately separate from Undo/Redo (Runde 7 feedback): zoom/pan
        // is view state, not project state, and should never be touched by
        // undo/redo history - this button is the intentional, explicit way
        // to get back to a known view instead.
        var resetViewBtn = new Button { Content = "⤢ " + Loc.T("toolbar.resetView"), ToolTip = Loc.T("toolbar.resetView.tip"), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(12, 0, 0, 0) };
        resetViewBtn.Click += (_, _) =>
        {
            if (_notebook != null && _notebook.SelectedIndex >= 0 && _notebook.SelectedIndex < _stages.Count)
                _stages[_notebook.SelectedIndex].stage.Canvas?.FitToWindow();
        };
        bar.Items.Add(resetViewBtn);

        bar.Items.Add(new Separator { Margin = new Thickness(12, 0, 0, 0) });
        BuildReferenceOverlayControls(bar);

        UpdateUndoRedoButtons();
        return bar;
    }

    /// <summary>Reference-image overlay controls (Runde 9 feedback item 3:
    /// "Füge jedenfalls den Modus noch hinzu, dass man ein weiteres Bild
    /// als zweite Ebene... als Referenzbild laden kann... Füge diese
    /// Option in der Werkzeugleiste hinzu.") - a second image loaded as a
    /// translucent aid on top of whatever stage canvas is currently shown,
    /// to help hand-trace an authentic real-world map. See
    /// ReferenceOverlay.cs for the shared state and PaintCanvasControl.
    /// Redraw() for where it's actually blended in.</summary>
    private void BuildReferenceOverlayControls(ToolBar bar)
    {
        // BuildToolbar() (and therefore this) re-runs from scratch on every
        // StartEditor call, including a plain UI language change that
        // keeps editing the exact same TileProject - ReferenceOverlay's
        // own state (loaded image, Enabled, Opacity) is a static that
        // survives that rebuild untouched, so the fresh controls here need
        // to reflect it rather than always starting from "nothing loaded".
        bool hasOverlay = ReferenceOverlay.Rgb != null;

        var loadBtn = new Button { Content = "🖼 " + Loc.T("toolbar.refOverlay.load"), ToolTip = Loc.T("toolbar.refOverlay.load.tip"), Padding = new Thickness(8, 2, 8, 2) };
        var showCheck = new CheckBox { Content = Loc.T("toolbar.refOverlay.show"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = hasOverlay, IsChecked = hasOverlay && ReferenceOverlay.Enabled };
        var clearBtn = new Button { Content = Loc.T("toolbar.refOverlay.clear"), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6, 0, 0, 0), IsEnabled = hasOverlay };
        var opacityLabel = new TextBlock { Text = Loc.T("toolbar.refOverlay.opacity"), Margin = new Thickness(10, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var opacitySlider = new Slider { Minimum = 0, Maximum = 100, Value = ReferenceOverlay.Opacity * 100, Width = 100, VerticalAlignment = VerticalAlignment.Center, IsEnabled = hasOverlay };

        void RedrawActiveCanvas()
        {
            if (_notebook != null && _notebook.SelectedIndex >= 0 && _notebook.SelectedIndex < _stages.Count)
                _stages[_notebook.SelectedIndex].stage.Canvas?.RequestRedraw();
        }

        loadBtn.Click += (_, _) =>
        {
            if (Project == null) return;
            var ofd = new OpenFileDialog
            {
                Title = Loc.T("toolbar.refOverlay.load"),
                Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files (*.*)|*.*",
            };
            if (ofd.ShowDialog(this) != true) return;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                ReferenceOverlay.Load(ofd.FileName, Project.WidthPx, Project.HeightPx);
            }
            catch (Exception exc)
            {
                Dialogs.Error(this, Loc.T("toolbar.refOverlay.load"), $"{exc.Message}\n\n{exc}");
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            showCheck.IsEnabled = true;
            showCheck.IsChecked = true;
            clearBtn.IsEnabled = true;
            opacitySlider.IsEnabled = true;
            RedrawActiveCanvas();
        };
        bar.Items.Add(loadBtn);

        showCheck.Checked += (_, _) => { ReferenceOverlay.Enabled = true; RedrawActiveCanvas(); };
        showCheck.Unchecked += (_, _) => { ReferenceOverlay.Enabled = false; RedrawActiveCanvas(); };
        bar.Items.Add(showCheck);

        opacitySlider.ValueChanged += (_, e) => { ReferenceOverlay.Opacity = e.NewValue / 100.0; RedrawActiveCanvas(); };
        bar.Items.Add(opacityLabel);
        bar.Items.Add(opacitySlider);

        clearBtn.Click += (_, _) =>
        {
            ReferenceOverlay.Clear();
            showCheck.IsChecked = false;
            showCheck.IsEnabled = false;
            clearBtn.IsEnabled = false;
            opacitySlider.IsEnabled = false;
            RedrawActiveCanvas();
        };
        bar.Items.Add(clearBtn);
    }

    private void UpdateUndoRedoButtons()
    {
        if (_undoBtn != null) _undoBtn.IsEnabled = Undo?.CanUndo ?? false;
        if (_redoBtn != null) _redoBtn.IsEnabled = Undo?.CanRedo ?? false;
    }

    // -- menu -----------------------------------------------------------------

    private Menu BuildMenu(bool editor)
    {
        var menu = new Menu();
        var file = new MenuItem { Header = Loc.T("menu.file") };

        // Runde 9 feedback item 1: from within the running editor, "New"
        // only ever offered a bare blank tile - none of the other landing-
        // screen modes (Open existing tile/project, Import heightmap,
        // Trace map image) were reachable without quitting back to the
        // landing screen first. Every one of these now gets its own menu
        // item here, each going through the same discard-confirmation as
        // plain "New" before replacing the current tile.
        var newItem = new MenuItem { Header = Loc.T("menu.new") };
        newItem.Click += (_, _) => ConfirmedNewFlow(NewTileFlow);
        var openTile = new MenuItem { Header = Loc.T("menu.openTile") };
        openTile.Click += (_, _) => ConfirmedNewFlow(OpenExistingTileFlow);
        var openProject = new MenuItem { Header = Loc.T("menu.openProject") };
        openProject.Click += (_, _) => ConfirmedNewFlow(OpenProjectFlow);
        var importHeightmap = new MenuItem { Header = Loc.T("landing.importHeightmap") };
        importHeightmap.Click += (_, _) => ConfirmedNewFlow(ImportHeightmapFlow);
        var importMapTrace = new MenuItem { Header = Loc.T("landing.importMapTrace"), Foreground = System.Windows.Media.Brushes.DarkOrange };
        importMapTrace.Click += (_, _) => ConfirmedNewFlow(ImportMapTraceFlow);
        var saveProject = new MenuItem { Header = Loc.T("menu.saveProject") };
        saveProject.Click += (_, _) => SaveProject();
        var saveProjectAs = new MenuItem { Header = Loc.T("menu.saveProjectAs") };
        saveProjectAs.Click += (_, _) => SaveProjectAs();
        var goExport = new MenuItem { Header = Loc.T("menu.goExport") };
        goExport.Click += (_, _) => GoToExport();
        var quit = new MenuItem { Header = Loc.T("menu.quit") };
        quit.Click += (_, _) => Close();

        file.Items.Add(newItem);
        file.Items.Add(openTile);
        file.Items.Add(openProject);
        file.Items.Add(importHeightmap);
        file.Items.Add(importMapTrace);
        file.Items.Add(new Separator());
        file.Items.Add(saveProject);
        file.Items.Add(saveProjectAs);
        file.Items.Add(new Separator());
        file.Items.Add(goExport);
        file.Items.Add(new Separator());
        file.Items.Add(quit);

        var edit = new MenuItem { Header = Loc.T("menu.edit") };
        var undoItem = new MenuItem { Header = Loc.T("menu.undo"), InputGestureText = "Ctrl+Z" };
        undoItem.Click += (_, _) => Undo?.Undo();
        var redoItem = new MenuItem { Header = Loc.T("menu.redo"), InputGestureText = "Ctrl+Y" };
        redoItem.Click += (_, _) => Undo?.Redo();
        edit.Items.Add(undoItem);
        edit.Items.Add(redoItem);

        var language = new MenuItem { Header = Loc.T("menu.language") };
        foreach (var lang in Loc.AllLanguages)
        {
            var item = new MenuItem { Header = Loc.DisplayName(lang), IsCheckable = true, IsChecked = lang == Loc.Current };
            item.Click += (_, _) => Loc.SetLanguage(lang);
            language.Items.Add(item);
        }

        var donate = new MenuItem { Header = Loc.T("menu.donate") };
        var sponsors = new MenuItem { Header = Loc.T("menu.donate.github"), ToolTip = Loc.T("menu.donate.github.tip") };
        sponsors.Click += (_, _) => OpenDonationPage(AppInfo.GitHubSponsorsUrl);
        var kofi = new MenuItem { Header = Loc.T("menu.donate.kofi"), ToolTip = Loc.T("menu.donate.kofi.tip") };
        kofi.Click += (_, _) => OpenDonationPage(AppInfo.KofiUrl);
        donate.Items.Add(sponsors);
        donate.Items.Add(kofi);

        if (editor)
        {
            menu.Items.Add(file);
            menu.Items.Add(edit);
        }
        menu.Items.Add(language);
        menu.Items.Add(BuildHelpMenu());
        menu.Items.Add(donate);
        return menu;
    }

    /// <summary>Opens a support page from the Donate menu (addresses live in AppInfo).</summary>
    private void OpenDonationPage(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            Dialogs.Error(this, Loc.T("menu.donate"), Loc.T("menu.donate.error") + "\n" + url);
        }
    }

    /// <summary>Shared discard-confirmation in front of any of the File
    /// menu's "start from something else" actions (New / Open tile / Open
    /// project / Import heightmap / Trace map image) - all five replace
    /// the currently open tile outright, so all five ask first. `flow` is
    /// only invoked after the user confirms; if they cancel the flow's own
    /// dialog (e.g. the file picker), nothing about the current tile has
    /// been touched.</summary>
    private void ConfirmedNewFlow(Action flow)
    {
        if (!Dialogs.Confirm(this, "New tile", "Discard the current tile and start over? Unsaved work will be lost."))
            return;
        flow();
    }

    private void SaveProject()
    {
        if (CurrentPath == null) { SaveProjectAs(); return; }
        Project!.Save(CurrentPath);
        Dialogs.Info(this, Loc.T("menu.saveProject"), string.Format(Loc.T("save.savedTo"), CurrentPath));
    }

    private void SaveProjectAs()
    {
        var sfd = new SaveFileDialog
        {
            Title = Loc.T("menu.saveProjectAs"),
            DefaultExt = ".rnwproj",
            Filter = Loc.T("save.filter"),
            FileName = $"{Project!.Name}.rnwproj",
        };
        if (sfd.ShowDialog(this) != true) return;
        Project.Save(sfd.FileName);
        CurrentPath = sfd.FileName;
        Dialogs.Info(this, Loc.T("menu.saveProject"), string.Format(Loc.T("save.savedTo"), sfd.FileName));
    }

    // Export() itself now creates a <Name>/ folder (with the .txt directly
    // inside it and a data/ subfolder for the bitmaps, matching the game's
    // own RNW tile layout) - the default here is just the parent folder to
    // export into, so it isn't doubled up as .../RNW_Tiles/<Name>/<Name>/.
    public string DefaultExportDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "RNW_Tiles");
}
