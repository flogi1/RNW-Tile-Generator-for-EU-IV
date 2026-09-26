using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>
/// A complete snapshot of every setting on the Random Generation tab -
/// everything StageRandom.Generate() feeds into TileProject.GenerateRandom
/// plus the handful of TileProject.Settings fields it also sets directly
/// (BorderCurviness, WastelandByHeight*, SeaSizeGradient and friends).
/// This is the unit a "Generation Template Preset" captures (Runde 8): save
/// the current sliders as one, and loading it later restores every field
/// and its on-screen control in one click - useful when generating many
/// differently-sized tiles that should otherwise share the same "look"
/// (water%, mountain amount, coastline detail, ...). See PresetStore.cs for
/// how presets are saved/loaded/shipped, and StageRandom.CapturePreset/
/// ApplyPreset for how this maps onto the actual UI controls.
///
/// Deliberately does NOT capture the seed textbox or the target tile
/// size/grid (those are chosen once per tile, not part of a reusable
/// "style" someone would want to stamp onto many differently-sized tiles).
/// </summary>
public sealed class GenerationPreset
{
    public double WaterPercent { get; set; } = 55;
    public double TargetLandProvinces { get; set; } = 200;
    public bool CapAt1000 { get; set; } = false;
    public double IslandCount { get; set; } = 1;
    public double Obscurity { get; set; } = 3;
    public double MountainAmount { get; set; } = 0.35;
    public double CoastDetail { get; set; } = 0.25;
    public bool WaterSurrounds { get; set; } = true;
    public TileProject.RandomTileOptions.EdgeRestriction EdgeMode { get; set; } = TileProject.RandomTileOptions.EdgeRestriction.None;
    public bool GenerateRivers { get; set; } = false;
    public double RiverCount { get; set; } = 6;
    public double TributaryFrequency { get; set; } = 0;
    public double DistributaryFrequency { get; set; } = 0;
    public double MinRiverLength { get; set; } = 0;
    public double RiverSmoothness { get; set; } = 6;
    public bool ConnectInlandSeas { get; set; } = true;
    public double LakeFrequency { get; set; } = 0.3;
    public double StraitFrequency { get; set; } = 0;
    public double StraitMaxDistance { get; set; } = 200;
    public double StraitMinSpacing { get; set; } = 48;
    public double StraitMaxPerIsland { get; set; } = 3;
    public double ModifierFrequency { get; set; } = 0;
    public double AutoRegionCount { get; set; } = 0;
    public double Moisture { get; set; } = 0;
    public double BorderCurviness { get; set; } = 0.4;
    public double LandSizeVariance { get; set; } = 0.0;
    public bool WastelandByHeight { get; set; } = false;
    public double WastelandHeightThreshold { get; set; } = 130;
    public bool SeaSizeGradient { get; set; } = true;
    public double SeaCoastalMultiplier { get; set; } = 5.0;
    public double SeaOceanMultiplier { get; set; } = 20.0;
    public bool ExperimentalEmptyFarSea { get; set; } = false;
}
