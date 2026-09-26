namespace RnwTileGenerator.Core;

/// <summary>
/// Constants describing the technical rules of the Random New World (RNW)
/// tile format, taken from the "Tile Making - an introduction" guide (by
/// Poh) and cross-checked against real shipped tile examples
/// (tileelzephor1/2). If the game files disagree with a value here, trust
/// the game files - the guide is a bit dated in places.
/// </summary>
public static class Constants
{
    // -- Grid -----------------------------------------------------------
    public const int GridUnit = 128;
    public const int RnwGridW = 18;
    public const int RnwGridH = 16;
    public const int MaxTileGridW = RnwGridW;
    public const int MaxTileGridH = RnwGridH;

    public const int ProvinceSoftWarn = 900;
    public const int ProvinceHardCap = 1000;

    // -- Height map (_h.bmp) ---------------------------------------------
    public const byte HeightDeepOcean = 50;
    public const byte HeightShallowMin = 75;
    public const byte HeightShallowMax = 93;
    public const byte HeightUnderwaterRiskLow = 94;
    public const byte HeightAboveWaterRiskHigh = 95;
    public const byte HeightLandBase = 96;
    public const byte HeightLandMostUsedMax = 180;
    public const byte HeightPeak = 230;
    public const byte HeightMaxByte = 255;
    public const byte HeightRiverMouthMax = 89;

    // -- River map (_r.bmp) -----------------------------------------------
    public static readonly Rgb RiverColorSource = new(0, 255, 0);
    public static readonly Rgb RiverColorMerge = new(255, 0, 0);
    public static readonly Rgb RiverColorSplit = new(255, 252, 0);

    public static readonly Rgb[] RiverColorsBlue =
    {
        new(0, 225, 255),
        new(0, 200, 255),
        new(0, 150, 255),
        new(0, 100, 255),
        new(0, 0, 255),
        new(0, 0, 225),
        new(0, 0, 200),
        new(0, 0, 150),
        new(0, 0, 100),
    };
    public const int RiverMinSize = 1;
    public static readonly int RiverMaxSize = RiverColorsBlue.Length; // 9

    public static readonly Rgb RiverColorSeaBg = new(122, 122, 122);
    public static readonly Rgb RiverColorLandBg = new(255, 255, 255);

    public const byte RiverPalSource = 0;
    public const byte RiverPalMerge = 1;
    public const byte RiverPalSplit = 2;
    public const byte RiverPalBlueStart = 3; // .. +8 inclusive
    public const byte RiverPalSeaBg = 254;
    public const byte RiverPalLandBg = 255;

    // -- Province map (_p.bmp) --------------------------------------------
    public static readonly Rgb DefaultEmptyColor = new(0, 0, 0);

    public const double LandProvincesPerCellDefault = 7.0;
    public const int LandProvinceMinSide = 30;
    public const double WastelandProvincesPerCellDefault = 3.0;
    public const double SeaProvinceTargetSpacingDefault = 125.0;

    /// <summary>Hard floor on any single province's pixel count, applied
    /// as a final cleanup pass after automatic/manual/regional province
    /// generation (see ProvinceMapGen.MergeTinyProvinces) - not a
    /// generation-style choice a user can tune, just a correctness floor
    /// against unplayable slivers like a single-pixel lake (Runde 7,
    /// dritte Rückmeldung).</summary>
    public const int MinProvincePixels = 25;
}
