using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>Random full-tile generation: one click builds a complete
/// coastline, mountains/height, and province layout from noise, aimed at
/// the requested water percentage, land province count, island count,
/// obscurity, mountain amount, coastline detail, rivers, edge-of-map
/// restriction, inland-sea-to-ocean connections, and (optionally) natural
/// straits, flavor modifiers, and region seeds. Meant as an optional
/// starting point - every other tab still works normally afterwards for
/// manual fine-tuning, exactly like the tile guide expects for a
/// hand-built tile.
///
/// Every slider here also remembers the value used last time (see
/// GenerationPrefs) across different tiles and app restarts - this tab is
/// exactly the "one click, these settings" workflow that request is about.</summary>
public sealed class StageRandom : IStage
{
    private readonly MainWindow _app;
    private readonly PaintCanvasControl _canvas;
    private readonly TextBlock _info;
    private readonly TextBlock _seedInfo;

    private double _waterPercent = GenerationPrefs.GetDouble("random.waterPercent", 55);
    private double _targetLandProvinces = GenerationPrefs.GetDouble("random.targetLandProvinces", 200);
    private double _islandCount = GenerationPrefs.GetDouble("random.islandCount", 1);
    private double _obscurity = GenerationPrefs.GetDouble("random.obscurity", 3);
    private double _mountainAmount = GenerationPrefs.GetDouble("random.mountainAmount", 0.35);
    private bool _waterSurrounds = GenerationPrefs.GetBool("random.waterSurrounds", true);
    private TileProject.RandomTileOptions.EdgeRestriction _edgeMode = TileProject.RandomTileOptions.EdgeRestriction.None;
    private double _coastDetail = GenerationPrefs.GetDouble("random.coastDetail", 0.25);
    private bool _generateRivers = GenerationPrefs.GetBool("random.generateRivers", false);
    private double _riverCount = GenerationPrefs.GetDouble("random.riverCount", 6);
    private double _tributaryFrequency = GenerationPrefs.GetDouble("random.tributaryFrequency", 0);
    private double _distributaryFrequency = GenerationPrefs.GetDouble("random.distributaryFrequency", 0);
    private double _minRiverLength = GenerationPrefs.GetDouble("random.minRiverLength", 0);
    private double _riverSmoothness = GenerationPrefs.GetDouble("random.riverSmoothness", 6);
    private bool _connectInlandSeas = GenerationPrefs.GetBool("random.connectInlandSeas", true);
    // Runde 7 (vierte Rückmeldung): default lowered from the implicit
    // "keep everything" behavior (1.0) to reduce how many separate small
    // Lake provinces a tile ends up with - see RandomTileOptions.
    // LakeFrequency's own doc comment for the full reasoning.
    private double _lakeFrequency = GenerationPrefs.GetDouble("random.lakeFrequency", 0.3);
    private double _straitFrequency = GenerationPrefs.GetDouble("random.straitFrequency", 0);
    // Runde 7 (second round of feedback): user testing found the first
    // round's defaults (100px / 15px) rejected far too many plausible
    // straits - raised to roughly 80% of each slider's own scale.
    private double _straitMaxDistance = GenerationPrefs.GetDouble("random.straitMaxDistance", 200);
    private double _straitMinSpacing = GenerationPrefs.GetDouble("random.straitMinSpacing", 48);
    private double _straitMaxPerIsland = GenerationPrefs.GetDouble("random.straitMaxPerIsland", 3);
    private double _modifierFrequency = GenerationPrefs.GetDouble("random.modifierFrequency", 0);
    private double _autoRegionCount = GenerationPrefs.GetDouble("random.autoRegionCount", 0);
    // Runde 7 (vierte Rückmeldung): default lowered from 5000 - the user
    // observed a strong apparent effect on how many lakes appear (though
    // Moisture itself only ever passes straight through to the game's own
    // add_moisture tile field and has no such effect in this generator's
    // own logic) and asked for a much gentler default either way.
    private double _moisture = GenerationPrefs.GetDouble("random.moisture", 0);

    // Runde 7 (dritte Rückmeldung): these mirror settings that already
    // existed on the Provinces tab (TileProject.Settings) but were never
    // actually visible/adjustable here, even though GenerateRandom's own
    // province-generation step (GenerateProvincesAuto) reads them from the
    // very same Settings object - so a value left over from a previous
    // visit to the Provinces tab (or just the class default) silently
    // applied to every random generation with no way to see or change it
    // from this tab. LandDensityPerCell/WastelandDensityPerCell/
    // LandObscurity are deliberately NOT duplicated here - GenerateRandom
    // already derives those itself from TargetLandProvinces/ObscurityLevel
    // (see TileProject.GenerateRandom), so a separate slider for them here
    // would just be silently overwritten and do nothing.
    private double _borderCurviness = GenerationPrefs.GetDouble("random.borderCurviness", 0.4);
    private double _landSizeVariance = GenerationPrefs.GetDouble("random.landSizeVariance", 0.0);
    private bool _wastelandByHeight = GenerationPrefs.GetBool("random.wastelandByHeight", false);
    private double _wastelandHeightThreshold = GenerationPrefs.GetDouble("random.wastelandHeightThreshold", 130);
    private bool _seaSizeGradient = GenerationPrefs.GetBool("random.seaSizeGradient", true);
    private double _seaCoastalMultiplier = GenerationPrefs.GetDouble("random.seaCoastalMultiplier", 5.0);
    private double _seaOceanMultiplier = GenerationPrefs.GetDouble("random.seaOceanMultiplier", 20.0);
    private bool _experimentalEmptyFarSea = GenerationPrefs.GetBool("random.experimentalEmptyFarSea", false);
    private bool _capAt1000 = GenerationPrefs.GetBool("random.capAt1000", false);

    private readonly TextBox _seedBox;
    private readonly Slider _riverCountSlider;
    private readonly Slider _tributaryFrequencySlider;
    private readonly Slider _distributaryFrequencySlider;
    private readonly Slider _minRiverLengthSlider;
    private readonly Slider _riverSmoothnessSlider;
    private readonly Slider _wastelandHeightThresholdSlider;
    private readonly ListBox _seedList;
    private readonly TextBlock _modifierEstimate;

    // Runde 8: control references kept around so a loaded Generation
    // Template Preset (see PresetStore.cs/GenerationPreset.cs) can push its
    // values onto every slider/checkbox/radio on screen, not just the
    // private fields behind them - setting a control's Value/IsChecked
    // here fires its own existing ValueChanged/Checked handler, which
    // already updates the matching field and GenerationPrefs, so
    // ApplyPreset below doesn't need to duplicate that wiring.
    private readonly ComboBox _presetCombo;
    private readonly Slider _waterPercentSlider;
    private readonly Slider _targetProvincesSlider;
    private readonly CheckBox _capCheck;
    private readonly Slider _islandCountSlider;
    private readonly Slider _obscuritySlider;
    private readonly Slider _mountainAmountSlider;
    private readonly Slider _coastDetailSlider;
    private readonly CheckBox _waterSurroundsCheck;
    private readonly RadioButton _edgeNoneRadio;
    private readonly RadioButton _edgeNorthRadio;
    private readonly RadioButton _edgeSouthRadio;
    private readonly CheckBox _generateRiversCheck;
    private readonly CheckBox _connectInlandSeasCheck;
    private readonly Slider _lakeFrequencySlider;
    private readonly Slider _curvinessSlider;
    private readonly Slider _sizeVarianceSlider;
    private readonly CheckBox _wastelandByHeightCheck;
    private readonly CheckBox _seaGradientCheck;
    private readonly Slider _seaCoastalMultiplierSlider;
    private readonly Slider _seaOceanMultiplierSlider;
    private readonly CheckBox _farSeaCheck;
    private readonly Slider _straitFrequencySlider;
    private readonly Slider _straitMaxDistanceSlider;
    private readonly Slider _straitMinSpacingSlider;
    private readonly Slider _straitMaxPerIslandSlider;
    private readonly Slider _modifierFrequencySlider;
    private readonly Slider _autoRegionCountSlider;
    private readonly Slider _moistureSlider;

    public UIElement View { get; }
    public PaintCanvasControl Canvas => _canvas;

    public StageRandom(MainWindow app)
    {
        _app = app;

        var side = Ui.Stack();
        side.Children.Add(Ui.Bold(Loc.T("random.title")));
        side.Children.Add(Ui.Wrap(Loc.T("random.intro"), 260));

        // Runde 8: Generation Template Presets - saved/built-in snapshots
        // of every setting below, so a favorite "look" can be reused
        // across many differently-sized tiles in one click. Placed at the
        // very top since loading one is meant to be the very first thing
        // done on this tab, before touching any individual slider.
        var presetPanel = Ui.Stack();
        _presetCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        presetPanel.Children.Add(_presetCombo);
        var presetLoadSaveRow = Ui.Horizontal();
        presetLoadSaveRow.Children.Add(Ui.Button(Loc.T("random.preset.load"), (_, _) => LoadSelectedPreset()));
        presetLoadSaveRow.Children.Add(Ui.Button(Loc.T("random.preset.save"), (_, _) => SaveCurrentAsPreset()));
        presetPanel.Children.Add(presetLoadSaveRow);
        presetPanel.Children.Add(Ui.Button(Loc.T("random.preset.delete"), (_, _) => DeleteSelectedPreset()));
        var presetGroup = Ui.Group(Loc.T("random.preset.group"), presetPanel);
        presetGroup.ToolTip = Loc.T("random.preset.tip");
        side.Children.Add(presetGroup);
        RefreshPresetList();

        _waterPercentSlider = Ui.LabeledSlider(side, Loc.T("random.waterPercent"), 5, 95, _waterPercent, (_, e) => { _waterPercent = e.NewValue; GenerationPrefs.Set("random.waterPercent", _waterPercent); }, Loc.T("random.waterPercent.tip"));
        _targetProvincesSlider = Ui.LabeledSlider(side, Loc.T("random.targetProvinces"), 10, 900, _targetLandProvinces, (_, e) => { _targetLandProvinces = e.NewValue; GenerationPrefs.Set("random.targetLandProvinces", _targetLandProvinces); UpdateModifierEstimate(); }, Loc.T("random.targetProvinces.tip"));
        _capCheck = Ui.CheckBoxCtl(Loc.T("random.capAt1000"), _capAt1000, (s, _) => { _capAt1000 = ((CheckBox)s).IsChecked ?? false; GenerationPrefs.SetBool("random.capAt1000", _capAt1000); });
        _capCheck.ToolTip = Loc.T("random.capAt1000.tip");
        side.Children.Add(_capCheck);
        _islandCountSlider = Ui.LabeledSlider(side, Loc.T("random.islandCount"), 1, 20, _islandCount, (_, e) => { _islandCount = e.NewValue; GenerationPrefs.Set("random.islandCount", _islandCount); }, Loc.T("random.islandCount.tip"));
        _obscuritySlider = Ui.LabeledSlider(side, Loc.T("random.obscurity"), 1, 10, _obscurity, (_, e) => { _obscurity = e.NewValue; GenerationPrefs.Set("random.obscurity", _obscurity); }, Loc.T("random.obscurity.tip"));
        _mountainAmountSlider = Ui.LabeledSlider(side, Loc.T("random.mountainAmount"), 0, 1, _mountainAmount, (_, e) => { _mountainAmount = e.NewValue; GenerationPrefs.Set("random.mountainAmount", _mountainAmount); }, Loc.T("random.mountainAmount.tip"));
        _coastDetailSlider = Ui.LabeledSlider(side, Loc.T("random.coastDetail"), 0, 1, _coastDetail, (_, e) => { _coastDetail = e.NewValue; GenerationPrefs.Set("random.coastDetail", _coastDetail); }, Loc.T("random.coastDetail.tip"));

        _waterSurroundsCheck = Ui.CheckBoxCtl(Loc.T("random.waterSurrounds"), _waterSurrounds, (s, _) => { _waterSurrounds = ((CheckBox)s).IsChecked ?? false; GenerationPrefs.SetBool("random.waterSurrounds", _waterSurrounds); });
        side.Children.Add(_waterSurroundsCheck);

        var edgePanel = Ui.Stack();
        _edgeNoneRadio = Ui.Radio(Loc.T("random.edgeMode.none"), "random-edge", true, (_, _) => _edgeMode = TileProject.RandomTileOptions.EdgeRestriction.None);
        _edgeNorthRadio = Ui.Radio(Loc.T("random.edgeMode.north"), "random-edge", false, (_, _) => _edgeMode = TileProject.RandomTileOptions.EdgeRestriction.North);
        _edgeSouthRadio = Ui.Radio(Loc.T("random.edgeMode.south"), "random-edge", false, (_, _) => _edgeMode = TileProject.RandomTileOptions.EdgeRestriction.South);
        string edgeTip = Loc.T("random.edgeMode.tip");
        _edgeNoneRadio.ToolTip = edgeTip; _edgeNorthRadio.ToolTip = edgeTip; _edgeSouthRadio.ToolTip = edgeTip;
        edgePanel.Children.Add(_edgeNoneRadio);
        edgePanel.Children.Add(_edgeNorthRadio);
        edgePanel.Children.Add(_edgeSouthRadio);
        side.Children.Add(Ui.Group(Loc.T("random.edgeMode"), edgePanel));

        _generateRiversCheck = Ui.CheckBoxCtl(Loc.T("random.generateRivers"), _generateRivers, (s, _) => { _generateRivers = ((CheckBox)s).IsChecked ?? false; GenerationPrefs.SetBool("random.generateRivers", _generateRivers); UpdateRiverCountEnabled(); });
        side.Children.Add(_generateRiversCheck);
        _riverCountSlider = Ui.LabeledSlider(side, Loc.T("random.riverCount"), 1, 30, _riverCount, (_, e) => { _riverCount = e.NewValue; GenerationPrefs.Set("random.riverCount", _riverCount); }, Loc.T("random.riverCount.tip"));
        _tributaryFrequencySlider = Ui.LabeledSlider(side, Loc.T("random.tributaryFrequency"), 0, 1, _tributaryFrequency, (_, e) => { _tributaryFrequency = e.NewValue; GenerationPrefs.Set("random.tributaryFrequency", _tributaryFrequency); }, Loc.T("random.tributaryFrequency.tip"));
        _distributaryFrequencySlider = Ui.LabeledSlider(side, Loc.T("random.distributaryFrequency"), 0, 1, _distributaryFrequency, (_, e) => { _distributaryFrequency = e.NewValue; GenerationPrefs.Set("random.distributaryFrequency", _distributaryFrequency); }, Loc.T("random.distributaryFrequency.tip"));
        _minRiverLengthSlider = Ui.LabeledSlider(side, Loc.T("random.minRiverLength"), 0, 1, _minRiverLength, (_, e) => { _minRiverLength = e.NewValue; GenerationPrefs.Set("random.minRiverLength", _minRiverLength); }, Loc.T("random.minRiverLength.tip"));
        _riverSmoothnessSlider = Ui.LabeledSlider(side, Loc.T("random.riverSmoothness"), 4, 10, _riverSmoothness, (_, e) => { _riverSmoothness = e.NewValue; GenerationPrefs.Set("random.riverSmoothness", _riverSmoothness); }, Loc.T("random.riverSmoothness.tip"));
        UpdateRiverCountEnabled();

        _connectInlandSeasCheck = Ui.CheckBoxCtl(Loc.T("random.connectInlandSeas"), _connectInlandSeas, (s, _) => { _connectInlandSeas = ((CheckBox)s).IsChecked ?? false; GenerationPrefs.SetBool("random.connectInlandSeas", _connectInlandSeas); });
        side.Children.Add(_connectInlandSeasCheck);
        _lakeFrequencySlider = Ui.LabeledSlider(side, Loc.T("random.lakeFrequency"), 0, 1, _lakeFrequency, (_, e) => { _lakeFrequency = e.NewValue; GenerationPrefs.Set("random.lakeFrequency", _lakeFrequency); }, Loc.T("random.lakeFrequency.tip"));

        // Runde 7 (dritte Rückmeldung): settings that already existed on
        // the Provinces tab but weren't visible/adjustable from here - see
        // the field declarations above for why LandDensityPerCell/
        // WastelandDensityPerCell/LandObscurity aren't duplicated here.
        var provAdvanced = Ui.Stack();
        _curvinessSlider = Ui.LabeledSlider(provAdvanced, Loc.T("provinces.borderCurviness"), 0, 1, _borderCurviness, (_, e) => { _borderCurviness = e.NewValue; GenerationPrefs.Set("random.borderCurviness", _borderCurviness); }, Loc.T("provinces.borderCurviness.tip"));
        _sizeVarianceSlider = Ui.LabeledSlider(provAdvanced, Loc.T("provinces.landSizeVariance"), 0, 1, _landSizeVariance, (_, e) => { _landSizeVariance = e.NewValue; GenerationPrefs.Set("random.landSizeVariance", _landSizeVariance); }, Loc.T("provinces.landSizeVariance.tip"));

        var wasteHeightPanel = Ui.Stack();
        _wastelandByHeightCheck = Ui.CheckBoxCtl(Loc.T("provinces.wastelandByHeight"), _wastelandByHeight, (s, _) =>
        {
            _wastelandByHeight = ((CheckBox)s).IsChecked ?? false;
            GenerationPrefs.SetBool("random.wastelandByHeight", _wastelandByHeight);
            // _wastelandHeightThresholdSlider is only actually assigned a
            // few lines below (it needs this checkbox to already exist as
            // the panel's first child) - by the time a user can actually
            // toggle this checkbox, construction has long since finished
            // and the field is set, but the compiler's nullable flow
            // analysis can't see that far ahead, hence the null-forgiving
            // `!` here rather than a dereference warning (same pattern as
            // StageProvinces.cs's own wastelandByHeight checkbox).
            _wastelandHeightThresholdSlider!.IsEnabled = _wastelandByHeight;
        });
        _wastelandByHeightCheck.ToolTip = Loc.T("provinces.wastelandByHeight.tip");
        wasteHeightPanel.Children.Add(_wastelandByHeightCheck);
        _wastelandHeightThresholdSlider = Ui.LabeledSliderWithValue(wasteHeightPanel, Loc.T("provinces.wastelandHeightThreshold"), 96, 235, _wastelandHeightThreshold, v => { _wastelandHeightThreshold = v; GenerationPrefs.Set("random.wastelandHeightThreshold", _wastelandHeightThreshold); }, Loc.T("provinces.wastelandHeightThreshold.tip"));
        _wastelandHeightThresholdSlider.IsEnabled = _wastelandByHeight;
        provAdvanced.Children.Add(Ui.Group(Loc.T("provinces.wastelandByHeightGroup"), wasteHeightPanel));

        var seaAdvPanel = Ui.Stack();
        _seaGradientCheck = Ui.CheckBoxCtl(Loc.T("provinces.seaSizeGradient"), _seaSizeGradient, (s, _) => { _seaSizeGradient = ((CheckBox)s).IsChecked ?? false; GenerationPrefs.SetBool("random.seaSizeGradient", _seaSizeGradient); });
        _seaGradientCheck.ToolTip = Loc.T("provinces.seaSizeGradient.tip");
        seaAdvPanel.Children.Add(_seaGradientCheck);
        _seaCoastalMultiplierSlider = Ui.LabeledSlider(seaAdvPanel, Loc.T("provinces.seaCoastalMultiplier"), 1, 15, _seaCoastalMultiplier, (_, e) => { _seaCoastalMultiplier = e.NewValue; GenerationPrefs.Set("random.seaCoastalMultiplier", _seaCoastalMultiplier); }, Loc.T("provinces.seaCoastalMultiplier.tip"));
        _seaOceanMultiplierSlider = Ui.LabeledSlider(seaAdvPanel, Loc.T("provinces.seaOceanMultiplier"), 5, 60, _seaOceanMultiplier, (_, e) => { _seaOceanMultiplier = e.NewValue; GenerationPrefs.Set("random.seaOceanMultiplier", _seaOceanMultiplier); }, Loc.T("provinces.seaOceanMultiplier.tip"));
        _farSeaCheck = Ui.CheckBoxCtl(Loc.T("provinces.experimentalEmptyFarSea"), _experimentalEmptyFarSea, (s, _) => { _experimentalEmptyFarSea = ((CheckBox)s).IsChecked ?? false; GenerationPrefs.SetBool("random.experimentalEmptyFarSea", _experimentalEmptyFarSea); });
        _farSeaCheck.ToolTip = Loc.T("provinces.experimentalEmptyFarSea.tip");
        seaAdvPanel.Children.Add(_farSeaCheck);
        provAdvanced.Children.Add(Ui.Group(Loc.T("provinces.seaGroup"), seaAdvPanel));

        side.Children.Add(Ui.Group(Loc.T("random.provinceAdvancedGroup"), provAdvanced));

        var flavorGroup = Ui.Stack();
        _straitFrequencySlider = Ui.LabeledSlider(flavorGroup, Loc.T("random.straitFrequency"), 0, 1, _straitFrequency, (_, e) => { _straitFrequency = e.NewValue; GenerationPrefs.Set("random.straitFrequency", _straitFrequency); }, Loc.T("random.straitFrequency.tip"));
        _straitMaxDistanceSlider = Ui.LabeledSliderWithValue(flavorGroup, Loc.T("random.straitMaxDistance"), 20, 1000, _straitMaxDistance, v => { _straitMaxDistance = v; GenerationPrefs.Set("random.straitMaxDistance", _straitMaxDistance); }, Loc.T("random.straitMaxDistance.tip"));
        _straitMinSpacingSlider = Ui.LabeledSliderWithValue(flavorGroup, Loc.T("random.straitMinSpacing"), 0, 200, _straitMinSpacing, v => { _straitMinSpacing = v; GenerationPrefs.Set("random.straitMinSpacing", _straitMinSpacing); }, Loc.T("random.straitMinSpacing.tip"));
        _straitMaxPerIslandSlider = Ui.LabeledSliderWithValue(flavorGroup, Loc.T("random.straitMaxPerIsland"), 1, 10, _straitMaxPerIsland, v => { _straitMaxPerIsland = v; GenerationPrefs.Set("random.straitMaxPerIsland", _straitMaxPerIsland); }, Loc.T("random.straitMaxPerIsland.tip"));
        _modifierFrequencySlider = Ui.LabeledSlider(flavorGroup, Loc.T("random.modifierFrequency"), 0, 1, _modifierFrequency, (_, e) => { _modifierFrequency = e.NewValue; GenerationPrefs.Set("random.modifierFrequency", _modifierFrequency); UpdateModifierEstimate(); }, Loc.T("random.modifierFrequency.tip"));
        _modifierEstimate = new TextBlock { Text = "", FontSize = 11, Foreground = System.Windows.Media.Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 6) };
        flavorGroup.Children.Add(_modifierEstimate);
        _autoRegionCountSlider = Ui.LabeledSlider(flavorGroup, Loc.T("random.autoRegionCount"), 0, 20, _autoRegionCount, (_, e) => { _autoRegionCount = e.NewValue; GenerationPrefs.Set("random.autoRegionCount", _autoRegionCount); }, Loc.T("random.autoRegionCount.tip"));
        side.Children.Add(Ui.Group(Loc.T("random.flavorGroup"), flavorGroup));
        UpdateModifierEstimate();

        // Moisture: slider + an editable exact-value textbox kept in sync
        // both ways, since 0-10000 is too coarse a range to hit an exact
        // number with a mouse drag alone.
        _moistureSlider = Ui.LabeledSliderWithValue(side, Loc.T("random.moisture"), 0, 10000, _moisture, v => { _moisture = v; GenerationPrefs.Set("random.moisture", _moisture); }, Loc.T("random.moisture.tip"));

        side.Children.Add(new TextBlock { Text = Loc.T("random.seed"), Margin = new Thickness(0, 8, 0, 2) });
        _seedBox = new TextBox { ToolTip = Loc.T("random.seed.tip") };
        side.Children.Add(_seedBox);

        var genNewBtn = Ui.Button(Loc.T("random.generateNew"), (_, _) => Generate(newSeed: true));
        genNewBtn.ToolTip = Loc.T("random.generateNew.tip");
        side.Children.Add(genNewBtn);
        var genSameBtn = Ui.Button(Loc.T("random.generateSame"), (_, _) => Generate(newSeed: false));
        genSameBtn.ToolTip = Loc.T("random.generateSame.tip");
        side.Children.Add(genSameBtn);

        _seedInfo = Ui.Info(260);
        side.Children.Add(_seedInfo);

        side.Children.Add(Ui.Sep());
        side.Children.Add(Ui.Bold(Loc.T("random.savedSeeds"), 12));
        _seedList = new ListBox { Height = 100 };
        RefreshSeedList();
        side.Children.Add(_seedList);
        var seedBtnRow = Ui.Horizontal();
        seedBtnRow.Children.Add(Ui.Button(Loc.T("random.saveSeed"), (_, _) => SaveCurrentSeed()));
        seedBtnRow.Children.Add(Ui.Button(Loc.T("random.loadSeed"), (_, _) => LoadSelectedSeed()));
        side.Children.Add(seedBtnRow);
        side.Children.Add(Ui.Button(Loc.T("random.deleteSeed"), (_, _) => DeleteSelectedSeed()));

        _info = Ui.Info(260);
        side.Children.Add(_info);

        _canvas = new PaintCanvasControl
        {
            ImageProvider = rect => _app.Project!.ProvinceResult != null
                ? Render.CompositeProvinces(_app.Project!, rect, false, null)
                : Render.CompositeCoastline(_app.Project!, rect, false),
        };

        View = Ui.SideAndCanvas(Ui.Sidebar(280, side), _canvas);
    }

    private void UpdateRiverCountEnabled()
    {
        _riverCountSlider.IsEnabled = _generateRivers;
        _tributaryFrequencySlider.IsEnabled = _generateRivers;
        _distributaryFrequencySlider.IsEnabled = _generateRivers;
        _minRiverLengthSlider.IsEnabled = _generateRivers;
        _riverSmoothnessSlider.IsEnabled = _generateRivers;
    }

    public void OnShow()
    {
        var p = _app.Project!;
        _lastGeneratedProject = null;
        _canvas.SetImageSize(p.WidthPx, p.HeightPx);
        UpdateInfo();
        UpdateSeedInfo();
        UpdateModifierEstimate();
        // Re-list presets every time this tab is shown, not just once at
        // construction - picks up both any preset saved/deleted elsewhere
        // in the session and a UI language change since the built-in
        // names are looked up live via Loc.T (Runde 8).
        RefreshPresetList();
        _canvas.RequestRedraw();
    }

    private void UpdateInfo()
    {
        var p = _app.Project!;
        long total = (long)p.WidthPx * p.HeightPx;
        int land = p.LandMask.CountTrue();
        double pct = total > 0 ? 100.0 * land / total : 0.0;
        string text = string.Format(Loc.T("random.landInfo"), p.WidthPx, p.HeightPx, land, pct);
        if (p.ProvinceResult != null) text += string.Format(Loc.T("random.provincesInfo"), p.TotalProvinceCount());
        _info.Text = text;
    }

    /// <summary>Rough "about N modifiers" note under the modifier-frequency
    /// slider (Runde 6 feedback: the slider alone gave no sense of how many
    /// provinces a given setting would actually flag). Uses the currently
    /// generated land province count if there is one, otherwise the target-
    /// province slider's value as a stand-in - either way it's only ever an
    /// estimate, since which provinces qualify (coastal, river mouth, ...)
    /// isn't known until generation actually runs.</summary>
    private void UpdateModifierEstimate()
    {
        var p = _app.Project;
        int landEstimate = p?.ProvinceResult != null
            ? p.ProvinceResult.Counts().GetValueOrDefault(ProvinceKind.Land)
            : (int)Math.Round(_targetLandProvinces);
        int estimate = (int)Math.Round(_modifierFrequency * Math.Max(landEstimate, 0));
        _modifierEstimate.Text = string.Format(Loc.T("random.modifierEstimate"), estimate);
    }

    private void UpdateSeedInfo()
    {
        var p = _app.Project!;
        _seedInfo.Text = p.Settings.RandomSeed.HasValue
            ? string.Format(Loc.T("random.lastSeed"), p.Settings.RandomSeed.Value)
            : "";
    }

    private void RefreshSeedList()
    {
        _seedList.Items.Clear();
        foreach (var s in SeedStore.All()) _seedList.Items.Add(s.ToString());
    }

    private void SaveCurrentSeed()
    {
        var p = _app.Project!;
        if (!p.Settings.RandomSeed.HasValue)
        {
            Dialogs.Info(_app, Loc.T("random.savedSeeds"), Loc.T("random.noSeedYet"));
            return;
        }
        var note = Dialogs.OneText(_app, Loc.T("random.saveSeed"), Loc.T("random.seedNote"));
        if (note == null) return; // cancelled
        SeedStore.Add(new SavedSeed
        {
            Seed = p.Settings.RandomSeed.Value,
            Note = note.Trim(),
            GridW = p.GridW,
            GridH = p.GridH,
            WaterPercent = _waterPercent,
        });
        RefreshSeedList();
    }

    private void LoadSelectedSeed()
    {
        int idx = _seedList.SelectedIndex;
        var all = SeedStore.All();
        if (idx < 0 || idx >= all.Count) return;
        _seedBox.Text = all[idx].Seed.ToString();
    }

    private void DeleteSelectedSeed()
    {
        int idx = _seedList.SelectedIndex;
        if (idx < 0) return;
        SeedStore.RemoveAt(idx);
        RefreshSeedList();
    }

    // -- Runde 8: Generation Template Presets --------------------------------

    /// <summary>One entry in the preset ComboBox. Key is the stable
    /// identity used to re-find/keep a selection across a RefreshPresetList
    /// call (the Loc key for a built-in preset, since its Display text
    /// changes with the UI language; the raw name for a user preset).
    /// Display is what's actually shown.</summary>
    private sealed class PresetListItem
    {
        public string Key { get; }
        public bool IsBuiltIn { get; }
        public string Display { get; }
        public GenerationPreset Values { get; }

        public PresetListItem(string key, bool isBuiltIn, string display, GenerationPreset values)
        {
            Key = key;
            IsBuiltIn = isBuiltIn;
            Display = display;
            Values = values;
        }

        public override string ToString() => Display;
    }

    private void RefreshPresetList()
    {
        var prev = _presetCombo.SelectedItem as PresetListItem;

        _presetCombo.Items.Clear();
        foreach (var b in PresetStore.BuiltIn)
        {
            string display = $"{Loc.T(b.NameKey)} ({Loc.T("random.preset.builtInTag")})";
            _presetCombo.Items.Add(new PresetListItem(b.NameKey, true, display, b.Values));
        }
        foreach (var u in PresetStore.LoadUser())
            _presetCombo.Items.Add(new PresetListItem(u.Name, false, u.Name, u.Values));

        int idx = 0;
        if (prev != null)
        {
            for (int i = 0; i < _presetCombo.Items.Count; i++)
            {
                if (_presetCombo.Items[i] is PresetListItem it && it.IsBuiltIn == prev.IsBuiltIn && it.Key == prev.Key)
                {
                    idx = i;
                    break;
                }
            }
        }
        if (_presetCombo.Items.Count > 0) _presetCombo.SelectedIndex = idx;
    }

    /// <summary>Snapshots every current Random Generation field into a
    /// GenerationPreset, for "save as new preset".</summary>
    private GenerationPreset CapturePreset() => new()
    {
        WaterPercent = _waterPercent,
        TargetLandProvinces = _targetLandProvinces,
        CapAt1000 = _capAt1000,
        IslandCount = _islandCount,
        Obscurity = _obscurity,
        MountainAmount = _mountainAmount,
        CoastDetail = _coastDetail,
        WaterSurrounds = _waterSurrounds,
        EdgeMode = _edgeMode,
        GenerateRivers = _generateRivers,
        RiverCount = _riverCount,
        TributaryFrequency = _tributaryFrequency,
        DistributaryFrequency = _distributaryFrequency,
        MinRiverLength = _minRiverLength,
        RiverSmoothness = _riverSmoothness,
        ConnectInlandSeas = _connectInlandSeas,
        LakeFrequency = _lakeFrequency,
        StraitFrequency = _straitFrequency,
        StraitMaxDistance = _straitMaxDistance,
        StraitMinSpacing = _straitMinSpacing,
        StraitMaxPerIsland = _straitMaxPerIsland,
        ModifierFrequency = _modifierFrequency,
        AutoRegionCount = _autoRegionCount,
        Moisture = _moisture,
        BorderCurviness = _borderCurviness,
        LandSizeVariance = _landSizeVariance,
        WastelandByHeight = _wastelandByHeight,
        WastelandHeightThreshold = _wastelandHeightThreshold,
        SeaSizeGradient = _seaSizeGradient,
        SeaCoastalMultiplier = _seaCoastalMultiplier,
        SeaOceanMultiplier = _seaOceanMultiplier,
        ExperimentalEmptyFarSea = _experimentalEmptyFarSea,
    };

    /// <summary>Pushes a preset's values onto every control on this tab.
    /// Each assignment below fires that control's own existing
    /// ValueChanged/Checked handler (WPF raises these for programmatic
    /// changes too), which already updates the backing field and
    /// GenerationPrefs - so this method only needs to drive the controls,
    /// not duplicate their wiring. A control left unchanged by the
    /// assignment (new value equals the old one) simply doesn't fire its
    /// event, which is harmless since the field already matched anyway.</summary>
    private void ApplyPreset(GenerationPreset preset)
    {
        _waterPercentSlider.Value = preset.WaterPercent;
        _targetProvincesSlider.Value = preset.TargetLandProvinces;
        _capCheck.IsChecked = preset.CapAt1000;
        _islandCountSlider.Value = preset.IslandCount;
        _obscuritySlider.Value = preset.Obscurity;
        _mountainAmountSlider.Value = preset.MountainAmount;
        _coastDetailSlider.Value = preset.CoastDetail;
        _waterSurroundsCheck.IsChecked = preset.WaterSurrounds;
        switch (preset.EdgeMode)
        {
            case TileProject.RandomTileOptions.EdgeRestriction.North: _edgeNorthRadio.IsChecked = true; break;
            case TileProject.RandomTileOptions.EdgeRestriction.South: _edgeSouthRadio.IsChecked = true; break;
            default: _edgeNoneRadio.IsChecked = true; break;
        }
        // The radio Checked handlers above already set _edgeMode, but only
        // when the click/assignment actually changes IsChecked from false
        // to true - if the preset's edge mode happens to match whichever
        // radio was already selected, no event fires and _edgeMode could
        // otherwise go stale, so it's set directly here too.
        _edgeMode = preset.EdgeMode;
        _generateRiversCheck.IsChecked = preset.GenerateRivers;
        _riverCountSlider.Value = preset.RiverCount;
        _tributaryFrequencySlider.Value = preset.TributaryFrequency;
        _distributaryFrequencySlider.Value = preset.DistributaryFrequency;
        _minRiverLengthSlider.Value = preset.MinRiverLength;
        _riverSmoothnessSlider.Value = preset.RiverSmoothness;
        _connectInlandSeasCheck.IsChecked = preset.ConnectInlandSeas;
        _lakeFrequencySlider.Value = preset.LakeFrequency;
        _curvinessSlider.Value = preset.BorderCurviness;
        _sizeVarianceSlider.Value = preset.LandSizeVariance;
        _wastelandByHeightCheck.IsChecked = preset.WastelandByHeight;
        _wastelandHeightThresholdSlider.Value = preset.WastelandHeightThreshold;
        _seaGradientCheck.IsChecked = preset.SeaSizeGradient;
        _seaCoastalMultiplierSlider.Value = preset.SeaCoastalMultiplier;
        _seaOceanMultiplierSlider.Value = preset.SeaOceanMultiplier;
        _farSeaCheck.IsChecked = preset.ExperimentalEmptyFarSea;
        _straitFrequencySlider.Value = preset.StraitFrequency;
        _straitMaxDistanceSlider.Value = preset.StraitMaxDistance;
        _straitMinSpacingSlider.Value = preset.StraitMinSpacing;
        _straitMaxPerIslandSlider.Value = preset.StraitMaxPerIsland;
        _modifierFrequencySlider.Value = preset.ModifierFrequency;
        _autoRegionCountSlider.Value = preset.AutoRegionCount;
        _moistureSlider.Value = preset.Moisture;

        // Same reasoning as _edgeMode above: these two enabled-states
        // depend on a checkbox that may not have actually changed value,
        // so they're refreshed directly rather than relying solely on the
        // checkbox's own Checked/Unchecked handler having fired.
        UpdateRiverCountEnabled();
        _wastelandHeightThresholdSlider.IsEnabled = _wastelandByHeight;
        UpdateModifierEstimate();
    }

    private void LoadSelectedPreset()
    {
        if (_presetCombo.SelectedItem is not PresetListItem item)
        {
            Dialogs.Info(_app, Loc.T("random.preset.load"), Loc.T("random.preset.noSelection"));
            return;
        }
        ApplyPreset(item.Values);
    }

    private void SaveCurrentAsPreset()
    {
        var name = Dialogs.OneText(_app, Loc.T("random.preset.save"), Loc.T("random.preset.nameLabel"));
        if (name == null) return; // cancelled
        name = name.Trim();
        if (name.Length == 0) return;

        bool collidesWithBuiltIn = PresetStore.BuiltIn.Any(b => string.Equals(Loc.T(b.NameKey), name, StringComparison.OrdinalIgnoreCase));
        if (collidesWithBuiltIn)
        {
            Dialogs.Error(_app, Loc.T("random.preset.save"), Loc.T("random.preset.nameReservedError"));
            return;
        }

        PresetStore.AddOrReplace(name, CapturePreset());
        RefreshPresetList();
        for (int i = 0; i < _presetCombo.Items.Count; i++)
        {
            if (_presetCombo.Items[i] is PresetListItem it && !it.IsBuiltIn && it.Key == name)
            {
                _presetCombo.SelectedIndex = i;
                break;
            }
        }
    }

    private void DeleteSelectedPreset()
    {
        if (_presetCombo.SelectedItem is not PresetListItem item)
        {
            Dialogs.Info(_app, Loc.T("random.preset.delete"), Loc.T("random.preset.noSelection"));
            return;
        }
        if (item.IsBuiltIn)
        {
            Dialogs.Error(_app, Loc.T("random.preset.delete"), Loc.T("random.preset.cannotDeleteBuiltIn"));
            return;
        }
        if (!Dialogs.Confirm(_app, Loc.T("random.preset.delete"), string.Format(Loc.T("random.preset.confirmDelete"), item.Display)))
            return;
        PresetStore.Delete(item.Key);
        RefreshPresetList();
    }

    /// <summary>Project whose current tile came from this tab's last Generate and
    /// has not been left since: re-generating it needs no "replace?" prompt (quick
    /// variant browsing, Ctrl+Z still works). Reset in OnShow - any hand edit
    /// happens on another tab.</summary>
    private TileProject? _lastGeneratedProject;

    private void Generate(bool newSeed)
    {
        var p = _app.Project!;
        if ((p.LandMask.AnyTrue() || p.ProvinceResult != null) && !ReferenceEquals(p, _lastGeneratedProject))
        {
            if (!Dialogs.Confirm(_app, Loc.T("random.generateButton"), Loc.T("random.confirmReplace")))
                return;
        }

        int? seed = null;
        if (newSeed) seed = Random.Shared.Next();
        else if (int.TryParse(_seedBox.Text, out var parsedSeed)) seed = parsedSeed;

        var options = new TileProject.RandomTileOptions
        {
            WaterPercent = _waterPercent,
            TargetLandProvinces = (int)Math.Round(_targetLandProvinces),
            IslandCount = (int)Math.Round(_islandCount),
            ObscurityLevel = (int)Math.Round(_obscurity),
            Seed = seed,
            WaterAlwaysSurroundsTile = _waterSurrounds,
            EdgeMode = _edgeMode,
            MountainAmount = _mountainAmount,
            GenerateRivers = _generateRivers,
            RiverCount = (int)Math.Round(_riverCount),
            TributaryFrequency = _tributaryFrequency,
            DistributaryFrequency = _distributaryFrequency,
            MinRiverLength = _minRiverLength,
            RiverCellSize = (int)Math.Round(_riverSmoothness),
            CoastlineDetail = _coastDetail,
            ConnectInlandSeas = _connectInlandSeas,
            LakeFrequency = _lakeFrequency,
            StraitFrequency = _straitFrequency,
            StraitMaxDistancePx = _straitMaxDistance,
            StraitMinSpacingPx = _straitMinSpacing,
            StraitMaxPerIsland = (int)Math.Round(_straitMaxPerIsland),
            ModifierFrequency = _modifierFrequency,
            AutoRegionCount = (int)Math.Round(_autoRegionCount),
        };

        _app.Undo?.SnapshotBeforeChange();
        p.Settings.Moisture = _moisture;
        // These pass straight through GenerateRandom's own province step
        // (GenerateProvincesAuto reads them off Settings) unchanged - see
        // the field declarations above for why LandDensityPerCell/
        // WastelandDensityPerCell/LandObscurity are NOT set here (Generate
        // Random computes those itself from TargetLandProvinces/
        // ObscurityLevel and would just overwrite them again).
        p.Settings.BorderCurviness = _borderCurviness;
        p.Settings.LandSizeVariance = _landSizeVariance;
        p.Settings.WastelandByHeightEnabled = _wastelandByHeight;
        p.Settings.WastelandHeightThreshold = _wastelandHeightThreshold;
        p.Settings.SeaSizeGradient = _seaSizeGradient;
        p.Settings.SeaCoastalAreaMultiplier = _seaCoastalMultiplier;
        p.Settings.SeaOceanAreaMultiplier = _seaOceanMultiplier;
        p.Settings.ExperimentalEmptyFarSea = _experimentalEmptyFarSea;

        // Noise generation over a full-size tile plus the province Voronoi
        // pass can take a couple of seconds - a wait cursor beats leaving
        // the user wondering if the click registered at all.
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            p.GenerateRandom(options);
            // Best-effort: merges away the smallest provinces until back
            // at/under the game's known-unsafe limit, rather than only
            // warning about it after the fact (Runde 7, dritte
            // Rückmeldung).
            if (_capAt1000) p.EnforceProvinceCap(Constants.ProvinceHardCap);
        }
        catch (Exception exc)
        {
            Dialogs.Error(_app, "Generierung fehlgeschlagen", exc.Message);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _lastGeneratedProject = p;
        UpdateInfo();
        UpdateSeedInfo();
        UpdateModifierEstimate();
        _seedBox.Text = p.Settings.RandomSeed?.ToString() ?? "";
        _canvas.RequestRedraw();
        var warn = p.ProvinceLimitWarning();
        if (warn != null) Dialogs.Warn(_app, "Province count", warn);
    }
}
