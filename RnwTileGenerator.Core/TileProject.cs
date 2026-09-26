using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;

namespace RnwTileGenerator.Core;

/// <summary>
/// Direct port of the original Python "rnw/project.py" module's
/// GenerationSettings dataclass.
/// </summary>
public sealed class GenerationSettings
{
    public double LandRiseDistance = 60.0;
    public double LandInteriorHeight = 150.0;
    public double MountainBoost = 85.0;
    public double SeaFadeDistance = 40.0;
    /// <summary>See HeightMapGen.Options.BlurCoastlines.</summary>
    public double BlurCoastlines = 2.5;
    /// <summary>See HeightMapGen.Options.BlurMountains.</summary>
    public double BlurMountains = 4.0;
    public double LandDensityPerCell = Constants.LandProvincesPerCellDefault;
    public double WastelandDensityPerCell = Constants.WastelandProvincesPerCellDefault;
    public double SeaSpacing = Constants.SeaProvinceTargetSpacingDefault;
    /// <summary>1-10. See ProvinceGenOptions.LandObscurity - how irregular
    /// automatic land province shapes/boundaries come out.</summary>
    public double LandObscurity = 3.0;
    /// <summary>0-1. See ProvinceGenOptions.BorderCurviness - how much the
    /// straight, faceted Voronoi boundary gets softened into an organic
    /// wavy line. Was EXPERIMENTAL and defaulted to 0 (Runde 7-17): the
    /// single-pass flip could leave a patch of one province's color
    /// disconnected from its own territory, bleeding into a neighbor as a
    /// stray pixel block. Fixed in Runde 18 by reverting/absorbing any
    /// flip patch that doesn't reconnect (RevertUnattachedFlipIslands/
    /// AbsorbStrandedSpecks in RasterOps.AddBorderCurviness) - confirmed
    /// bleed-free by the user across multiple real-build tests, so this is
    /// now on by default.</summary>
    public double BorderCurviness = 0.4;
    /// <summary>0-1. See ProvinceGenOptions.LandSizeVariance - how much
    /// automatically generated land provinces vary in size across the
    /// map, from region to region, instead of reading uniformly sized.
    /// 0 (default) keeps the original evenly-spaced-grid behavior.</summary>
    public double LandSizeVariance = 0.0;
    public int? RandomSeed;

    /// <summary>See ProvinceGenOptions.WastelandHeightThreshold - only
    /// applied when WastelandByHeightEnabled is on.</summary>
    public bool WastelandByHeightEnabled = false;
    public double WastelandHeightThreshold = 130;
    /// <summary>See ProvinceGenOptions.SeaSizeGradient.</summary>
    public bool SeaSizeGradient = true;
    /// <summary>See ProvinceGenOptions.ExperimentalEmptyFarSea.</summary>
    public bool ExperimentalEmptyFarSea = false;
    /// <summary>See ProvinceGenOptions.SeaCoastalAreaMultiplier.</summary>
    public double SeaCoastalAreaMultiplier = 5.0;
    /// <summary>See ProvinceGenOptions.SeaOceanAreaMultiplier.</summary>
    public double SeaOceanAreaMultiplier = 20.0;
    /// <summary>0-10000, exported as the tile's "add_moisture" extra
    /// field (see TileMetadata.ExtraFields / TextFileIO). Default lowered
    /// to 0 (Runde 7, vierte Rückmeldung: the user observed a strong
    /// apparent effect on how many lakes appear and asked for a much
    /// gentler default) - this value is only ever passed straight through
    /// to the game's own add_moisture tile field and has no other effect
    /// anywhere in this generator's own logic.</summary>
    public double Moisture = 0;
}

/// <summary>
/// The in-memory state of one tile being built or edited, plus save/load of
/// a working project file (.rnwproj - our own format, NOT the game's tile
/// files) and the final export to the game's four tile files. Direct port
/// of the original Python "rnw/project.py" module's TileProject class.
///
/// The working-project file format here is our own design (the Python
/// version used numpy's .npz + a zip wrapper): a plain zip containing raw
/// byte-array dumps of each mask/map plus a meta.json built with
/// System.Text.Json - both System.IO.Compression and System.Text.Json ship
/// with the .NET runtime itself, so this keeps the "no third-party
/// dependency" property intact.
/// </summary>
public sealed class TileProject
{
    public string Name;
    public int GridW;
    public int GridH;

    /// <summary>true = above water. Starts all-false (all sea). Includes
    /// wasteland cells too - WastelandMask is a subset of this.</summary>
    public GrayMap LandMask;
    /// <summary>Byte intensity 0..255, treated as 0..1 extra-elevation
    /// strength by HeightMapGen.</summary>
    public GrayMap MountainMask;
    public GrayMap WastelandMask;
    public GrayMap EmptyMask;

    public List<RiverSegment> RiverSegments = new();
    /// <summary>Row-major top-down palette indices - the base layer from a
    /// loaded tile, painted over by RiverSegments in RiverRaster().</summary>
    public byte[]? ImportedRiverRaster;

    public GrayMap? Height;
    public ProvinceMapResult? ProvinceResult;

    /// <summary>The manual province-border pen strokes drawn on the
    /// Provinces stage's "Manual" mode, kept as real project state (rather
    /// than UI-only scratch) so it (a) survives a project save/reload and
    /// (b) participates in the whole-project undo/redo snapshot the same
    /// way every other paintable layer does - see UndoManager (App
    /// project). Null until the user starts drawing borders.</summary>
    public GrayMap? ManualProvinceBorderMask;

    public TileMetadata Metadata = new();
    public GenerationSettings Settings = new();

    public TileProject(string name, int gridW, int gridH)
    {
        ValidateGridSize(gridW, gridH);
        Name = name;
        GridW = gridW;
        GridH = gridH;
        int w = WidthPx, h = HeightPx;

        LandMask = GrayMap.Bool(w, h, false);
        MountainMask = new GrayMap(w, h);
        WastelandMask = GrayMap.Bool(w, h, false);
        EmptyMask = GrayMap.Bool(w, h, false);
    }

    // -- geometry -----------------------------------------------------------

    public int WidthPx => GridW * Constants.GridUnit;
    public int HeightPx => GridH * Constants.GridUnit;

    public static void ValidateGridSize(int gridW, int gridH)
    {
        if (gridW < 1 || gridW > Constants.MaxTileGridW)
            throw new ArgumentException($"width must be 1-{Constants.MaxTileGridW} grid cells (128px each), got {gridW}");
        if (gridH < 1 || gridH > Constants.MaxTileGridH)
            throw new ArgumentException($"height must be 1-{Constants.MaxTileGridH} grid cells (128px each), got {gridH}");
    }

    // -- generation pipeline --------------------------------------------------

    public GrayMap GenerateHeight()
    {
        var mouths = RiverMapGen.RiverMouths(LandMask, RiverSegments);
        var rng = Settings.RandomSeed.HasValue ? new Random(Settings.RandomSeed.Value) : new Random();
        var opts = new HeightMapGen.Options
        {
            LandRiseDistance = Settings.LandRiseDistance,
            LandInteriorHeight = Settings.LandInteriorHeight,
            MountainBoost = Settings.MountainBoost,
            SeaFadeDistance = Settings.SeaFadeDistance,
            BlurCoastlines = Settings.BlurCoastlines,
            BlurMountains = Settings.BlurMountains,
        };
        Height = HeightMapGen.Generate(LandMask, MountainMask, opts, mouths, rng);
        return Height;
    }

    public GrayMap EnsureHeight()
    {
        if (Height == null) GenerateHeight();
        return Height!;
    }

    /// <summary>Defensive safety net: every pixel LandMask currently marks
    /// as dry land must have a height at or above Constants.HeightLandBase
    /// - anything at or below Constants.HeightShallowMax reads to the game
    /// as at or below water level. Height generation itself already clamps
    /// to this (see HeightMapGen.Generate), but that clamp only runs at the
    /// moment a height map is (re)generated - a coastline pen stroke that
    /// adds NEW land after a height map already exists, an imported/loaded
    /// height map, or the manual height brush painting a low value leave
    /// their pixels exactly as set, with nothing re-checking them
    /// afterwards. That is exactly the "provinces partially underwater
    /// in-game" bug reported after playtesting (Runde 7) - this is called
    /// both right after a coastline edit (so the height preview and any
    /// later height-dependent logic, e.g. wasteland-by-height, stay
    /// correct) and, as a final guarantee, right before export.</summary>
    public void ClampHeightToLandSafety()
    {
        if (Height == null) return;
        for (int i = 0; i < Height.Data.Length; i++)
            if (LandMask.Data[i] >= 128 && Height.Data[i] < Constants.HeightLandBase)
                Height.Data[i] = Constants.HeightLandBase;
    }

    public byte[] RiverRaster()
    {
        int w = WidthPx, h = HeightPx;
        if (ImportedRiverRaster != null)
        {
            var idx = (byte[])ImportedRiverRaster.Clone();
            foreach (var seg in RiverSegments)
            {
                byte pal = seg.BluePaletteIndex();
                foreach (var (x, y) in RiverMapGen.RasterizeSegment(seg))
                    if (x >= 0 && x < w && y >= 0 && y < h) idx[y * w + x] = pal;
            }
            foreach (var seg in RiverSegments)
            {
                if (seg.Points.Count == 0) continue;
                var (x, y) = seg.Points[0];
                if (x >= 0 && x < w && y >= 0 && y < h) idx[y * w + x] = RiverMapGen.JunctionPaletteIndex(seg.Junction);
            }
            return idx;
        }
        return RiverMapGen.BuildRiverIndexArray(LandMask, RiverSegments);
    }

    public ProvinceMapResult GenerateProvincesAuto()
    {
        // BuildProvinceGenOptions(null) reads the seed straight from
        // Settings.RandomSeed, same as this method always did.
        var opts = BuildProvinceGenOptions(null);
        ProvinceResult = ProvinceMapGen.GenerateAutomatic(LandMask, WastelandMask, EmptyMask, opts);
        return ProvinceResult;
    }

    /// <summary>
    /// Clears out just the plain-land part of a rectangular region and
    /// fills it back in with a fresh, independently-seeded set of roughly
    /// `targetCount` land provinces, leaving every province elsewhere on
    /// the tile (and any sea/lake/wasteland/empty pixels inside the
    /// rectangle) completely untouched - including their existing colors,
    /// so regenerating one small area never reshuffles the palette of
    /// provinces the user already likes. Requires a province layout to
    /// already exist (automatic or manual).
    /// </summary>
    public void GenerateProvincesInRegion(int x0, int y0, int x1, int y1, int targetCount, int? seed = null)
    {
        if (ProvinceResult == null)
            throw new InvalidOperationException("Generate or draw provinces first before regenerating a region.");

        int w = WidthPx, h = HeightPx;
        x0 = Math.Clamp(Math.Min(x0, x1), 0, w - 1);
        x1 = Math.Clamp(Math.Max(x0, x1), 0, w - 1);
        y0 = Math.Clamp(Math.Min(y0, y1), 0, h - 1);
        y1 = Math.Clamp(Math.Max(y0, y1), 0, h - 1);

        var rng = seed.HasValue ? new Random(seed.Value) : new Random();

        // Only plain land (not wasteland, not already-empty) inside the
        // rectangle is up for regeneration - sea, lakes, wasteland, and
        // anything outside the rectangle are left exactly as they were.
        var plainLand = GrayMap.And(GrayMap.And(LandMask, GrayMap.Not(WastelandMask)), GrayMap.Not(EmptyMask));
        var domain = GrayMap.Bool(w, h, false);
        for (int y = y0; y <= y1; y++)
        {
            int row = y * w;
            for (int x = x0; x <= x1; x++)
                if (plainLand.Data[row + x] >= 128) domain.Data[row + x] = 255;
        }
        if (!domain.AnyTrue())
            throw new InvalidOperationException("The selected rectangle doesn't contain any plain land to regenerate.");

        var labels = ProvinceResult.Labels;
        var provinces = ProvinceResult.Provinces;

        // Clear every label under the domain, then rebuild pixel
        // counts/seeds for every id from scratch by a single scan (cheap,
        // O(pixels)) - simpler and less error-prone than trying to patch
        // counts incrementally, and touches nothing about colors.
        for (int i = 0; i < domain.Data.Length; i++)
            if (domain.Data[i] >= 128) labels.Data[i] = -1;

        var newCounts = new Dictionary<int, int>();
        var newSeeds = new Dictionary<int, (int x, int y)>();
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int pid = labels.Data[row + x];
                if (pid < 0) continue;
                if (!newCounts.ContainsKey(pid)) { newCounts[pid] = 0; newSeeds[pid] = (x, y); }
                newCounts[pid]++;
            }
        }
        // Provinces that lost every pixel to the clear above (i.e. were
        // wholly inside the rectangle) are removed entirely; survivors
        // keep their existing Color untouched and just get a refreshed
        // PixelCount/Seed.
        foreach (var pid in new List<int>(provinces.Keys))
        {
            if (!newCounts.TryGetValue(pid, out var count))
            {
                provinces.Remove(pid);
                continue;
            }
            var info = provinces[pid];
            info.PixelCount = count;
            info.Seed = newSeeds[pid];
        }

        // Seed the new province ids just past whatever the highest
        // existing id is, and reserve every color already in use so the
        // fresh provinces can never collide with an existing one.
        int nextId = provinces.Count > 0 ? provinces.Keys.Max() + 1 : 0;
        var allocator = new ColorAllocator(new[] { Metadata.EmptyColor });
        foreach (var info in provinces.Values) allocator.Reserve(info.Color);

        double cellArea = (double)Constants.GridUnit * Constants.GridUnit;
        double domainCells = Math.Max(domain.CountTrue() / cellArea, 0.01);
        double density = Math.Clamp(Math.Max(targetCount, 1) / domainCells, 0.1, 60);
        double spacing = Math.Max(Math.Sqrt(cellArea / density), 8);
        double obscurityT = Math.Clamp((Settings.LandObscurity - 1) / 9.0, 0.0, 1.0);
        var seeds = ProvinceMapGen.JitteredSeeds(domain, spacing, rng, jitterFraction: 0.12 + obscurityT * 0.33);

        // Same river/mountain boundary bias as automatic generation
        // (see ProvinceMapGen.GenerateAutomatic), so a regenerated
        // rectangle reads consistently with the rest of the tile.
        byte[]? cost = null;
        var riverIdx = RiverRaster();
        bool hasRivers = RiverSegments.Count > 0 || ImportedRiverRaster != null;
        bool hasMountains = MountainMask.AnyTrue();
        if (hasRivers || hasMountains || obscurityT > 0)
        {
            int n = w * h;
            cost = new byte[n];
            float[]? noise = obscurityT > 0
                ? NoiseGen.GenerateField(w, h, Math.Max(spacing * 0.5, 6), 3, 0.5, 2.0, seed ?? Environment.TickCount)
                : null;
            double noiseSwing = obscurityT * 4.0;
            for (int i = 0; i < n; i++)
            {
                double c = 1.0;
                if (hasRivers && riverIdx[i] != Constants.RiverPalLandBg && riverIdx[i] != Constants.RiverPalSeaBg) c += 7.0;
                if (hasMountains) c += MountainMask.Data[i] / 255.0 * 5.0;
                if (noise != null) c += (noise[i] + 1.0) * 0.5 * noiseSwing;
                cost[i] = (byte)Math.Clamp(Math.Round(c), 1, 250);
            }
        }

        if (seeds.Count > 0)
        {
            var assign = cost != null ? RasterOps.WeightedVoronoiAssign(domain, seeds, cost) : RasterOps.VoronoiAssign(domain, seeds);
            var counts = new int[seeds.Count];
            for (int i = 0; i < assign.Length; i++) if (assign[i] >= 0) counts[assign[i]]++;

            var idMap = new int[seeds.Count];
            for (int local = 0; local < seeds.Count; local++)
            {
                if (counts[local] == 0) { idMap[local] = -1; continue; }
                int pid = nextId++;
                idMap[local] = pid;
                var color = allocator.Next();
                provinces[pid] = new ProvinceInfo
                {
                    ProvinceId = pid,
                    Color = color,
                    Kind = ProvinceKind.Land,
                    PixelCount = counts[local],
                    Seed = seeds[local],
                };
            }
            for (int i = 0; i < assign.Length; i++)
            {
                if (assign[i] < 0) continue;
                int pid = idMap[assign[i]];
                if (pid >= 0) labels.Data[i] = pid;
            }

            if (Settings.BorderCurviness > 0.001)
                RasterOps.AddBorderCurviness(labels, domain, Settings.BorderCurviness, seed ?? Environment.TickCount);
        }

        // Same correctness floor as every other province-generating path
        // (Runde 7, dritte Rückmeldung) - a rectangle regen can just as
        // easily leave a tiny sliver behind at its own edges.
        // restrictToSameKind: true for the same reason as
        // RegenerateLandProvinces/RegenerateSeaProvinces - this method
        // promises everything outside the rectangle (sea, lakes,
        // wasteland included) stays untouched, so a tiny land sliver right
        // at the rectangle's edge must never get absorbed into a
        // neighboring sea/wasteland province just because it's the
        // strongest vote; if it has no same-kind (land) neighbor, it's
        // simply left as-is rather than risking that cross-domain merge.
        ProvinceMapGen.MergeTinyProvinces(labels, provinces, Constants.MinProvincePixels, restrictToSameKind: true);

        // Repaint the RGB raster - cheap full pass, and the simplest way
        // to guarantee it matches `labels`/`provinces` exactly after a
        // partial edit like this.
        ProvinceMapGen.RepaintRgb(labels, provinces, ProvinceResult.Rgb, Metadata.EmptyColor);
    }

    public ProvinceMapResult SetManualProvinces(LabelMap labelArray, IReadOnlyDictionary<int, ProvinceKind> kindOf)
    {
        ProvinceResult = ProvinceMapGen.FromLabelArray(labelArray, kindOf, Metadata.EmptyColor);
        return ProvinceResult;
    }

    /// <summary>Builds a ProvinceGenOptions from the current Settings, the
    /// way GenerateProvincesAuto does - shared by RegenerateLandProvinces/
    /// RegenerateSeaProvinces so they use exactly the same settings as an
    /// ordinary automatic generation.</summary>
    private ProvinceGenOptions BuildProvinceGenOptions(int? seedOverride)
    {
        var s = Settings;
        var riverIdx = RiverRaster();
        GrayMap? riverMask = null;
        if (RiverSegments.Count > 0 || ImportedRiverRaster != null)
        {
            riverMask = GrayMap.Bool(WidthPx, HeightPx, false);
            for (int i = 0; i < riverIdx.Length; i++)
                if (riverIdx[i] != Constants.RiverPalLandBg && riverIdx[i] != Constants.RiverPalSeaBg)
                    riverMask.Data[i] = 255;
        }
        return new ProvinceGenOptions
        {
            LandDensityPerCell = s.LandDensityPerCell,
            WastelandDensityPerCell = s.WastelandDensityPerCell,
            SeaSpacing = s.SeaSpacing,
            LandObscurity = s.LandObscurity,
            BorderCurviness = s.BorderCurviness,
            LandSizeVariance = s.LandSizeVariance,
            RiverMask = riverMask,
            MountainMask = MountainMask.AnyTrue() ? MountainMask : null,
            EmptyColor = Metadata.EmptyColor,
            Seed = seedOverride ?? s.RandomSeed,
            Height = s.WastelandByHeightEnabled ? (Height ?? EnsureHeight()) : null,
            WastelandHeightThreshold = s.WastelandByHeightEnabled ? s.WastelandHeightThreshold : null,
            SeaSizeGradient = s.SeaSizeGradient,
            ExperimentalEmptyFarSea = s.ExperimentalEmptyFarSea,
            SeaCoastalAreaMultiplier = s.SeaCoastalAreaMultiplier,
            SeaOceanAreaMultiplier = s.SeaOceanAreaMultiplier,
        };
    }

    /// <summary>Re-rolls just the land+wasteland province cutting using the
    /// current Settings, leaving the coastline (LandMask itself is never
    /// touched by this) and every sea/lake province completely untouched -
    /// same ids, same colors, same shapes. For when the coastline is good
    /// but the land province layout should be re-rolled without a full
    /// random regeneration (Runde 7, dritte Rückmeldung: "Landprovinzen
    /// neu generieren", coastline preserved). Requires a province layout
    /// to already exist.</summary>
    public void RegenerateLandProvinces(int? seed = null)
    {
        if (ProvinceResult == null)
            throw new InvalidOperationException("Generate or draw provinces first before regenerating just the land provinces.");
        var labels = ProvinceResult.Labels;
        var provinces = ProvinceResult.Provinces;

        var toRemove = new List<int>();
        foreach (var kv in provinces) if (kv.Value.Kind is ProvinceKind.Land or ProvinceKind.Wasteland) toRemove.Add(kv.Key);
        var removedSet = new HashSet<int>(toRemove);
        foreach (int id in toRemove) provinces.Remove(id);
        for (int i = 0; i < labels.Data.Length; i++) if (removedSet.Contains(labels.Data[i])) labels.Data[i] = -1;

        int nextId = provinces.Count > 0 ? provinces.Keys.Max() + 1 : 0;
        int startId = nextId;
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var opts = BuildProvinceGenOptions(seed);

        ProvinceMapGen.GenerateLandDomain(labels, provinces, ref nextId, LandMask, WastelandMask, EmptyMask, opts, rng, out var plainLand, out var wastelandDomain);

        if (opts.BorderCurviness > 0.001)
        {
            int curvinessSeed = opts.Seed ?? Environment.TickCount;
            RasterOps.AddBorderCurviness(labels, plainLand, opts.BorderCurviness, curvinessSeed);
            RasterOps.AddBorderCurviness(labels, wastelandDomain, opts.BorderCurviness, curvinessSeed + 999);
        }
        RasterOps.FillUnlabeledGapsWithinDomain(labels, plainLand);
        RasterOps.FillUnlabeledGapsWithinDomain(labels, wastelandDomain);

        var freshCounts = new Dictionary<int, int>();
        for (int i = 0; i < labels.Data.Length; i++)
        {
            int pid = labels.Data[i];
            if (pid < 0) continue;
            freshCounts[pid] = freshCounts.GetValueOrDefault(pid) + 1;
        }
        foreach (var info in provinces.Values) info.PixelCount = freshCounts.GetValueOrDefault(info.ProvinceId);

        // restrictToSameKind: a tiny new land sliver must never get merged
        // into an existing SEA province just because it happens to touch
        // more water than land pixels - that would silently hand a land
        // pixel to a sea-kind province and break the "sea untouched"
        // promise this method makes.
        ProvinceMapGen.MergeTinyProvinces(labels, provinces, Constants.MinProvincePixels, restrictToSameKind: true);

        var newIds = new HashSet<int>();
        foreach (var kv in provinces) if (kv.Key >= startId) newIds.Add(kv.Key);
        ProvinceMapGen.ColorizeNewOnly(provinces, newIds, Metadata.EmptyColor);
        ProvinceMapGen.RepaintRgb(labels, provinces, ProvinceResult.Rgb, Metadata.EmptyColor);
    }

    /// <summary>Re-rolls just the sea+lake province cutting using the
    /// current Settings, leaving the coastline and every land/wasteland
    /// province completely untouched - same ids, same colors, same
    /// shapes. Symmetric counterpart to RegenerateLandProvinces (Runde 7,
    /// dritte Rückmeldung: "Seeprovinzen neu generieren"). Requires a
    /// province layout to already exist.</summary>
    public void RegenerateSeaProvinces(int? seed = null)
    {
        if (ProvinceResult == null)
            throw new InvalidOperationException("Generate or draw provinces first before regenerating just the sea provinces.");
        var labels = ProvinceResult.Labels;
        var provinces = ProvinceResult.Provinces;

        var toRemove = new List<int>();
        foreach (var kv in provinces) if (kv.Value.Kind is ProvinceKind.Sea or ProvinceKind.Lake) toRemove.Add(kv.Key);
        var removedSet = new HashSet<int>(toRemove);
        foreach (int id in toRemove) provinces.Remove(id);
        for (int i = 0; i < labels.Data.Length; i++) if (removedSet.Contains(labels.Data[i])) labels.Data[i] = -1;

        int nextId = provinces.Count > 0 ? provinces.Keys.Max() + 1 : 0;
        int startId = nextId;
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var opts = BuildProvinceGenOptions(seed);

        ProvinceMapGen.GenerateWaterDomain(labels, provinces, ref nextId, LandMask, EmptyMask, opts, rng, out var seaMask, out var lakeMask, out var distToLand);

        RasterOps.FillUnlabeledGapsWithinDomain(labels, seaMask);
        RasterOps.FillUnlabeledGapsWithinDomain(labels, lakeMask);

        var freshCounts = new Dictionary<int, int>();
        for (int i = 0; i < labels.Data.Length; i++)
        {
            int pid = labels.Data[i];
            if (pid < 0) continue;
            freshCounts[pid] = freshCounts.GetValueOrDefault(pid) + 1;
        }
        foreach (var info in provinces.Values) info.PixelCount = freshCounts.GetValueOrDefault(info.ProvinceId);

        ProvinceMapGen.PruneExperimentalFarSea(labels, provinces, LandMask, EmptyMask, opts, distToLand);

        // restrictToSameKind, mirroring RegenerateLandProvinces: a tiny
        // new sea/lake sliver must never get merged into an existing LAND
        // province.
        ProvinceMapGen.MergeTinyProvinces(labels, provinces, Constants.MinProvincePixels, restrictToSameKind: true);

        var newIds = new HashSet<int>();
        foreach (var kv in provinces) if (kv.Key >= startId) newIds.Add(kv.Key);
        ProvinceMapGen.ColorizeNewOnly(provinces, newIds, Metadata.EmptyColor);
        ProvinceMapGen.RepaintRgb(labels, provinces, ProvinceResult.Rgb, Metadata.EmptyColor);
    }

    /// <summary>Best-effort: if the tile currently has more provinces than
    /// `maxCount`, repeatedly merges away the smallest ones (raising the
    /// merge-size threshold each round) until the total is back at or
    /// under the cap, or a bailout round limit is hit - not exact (the
    /// resulting count can land a bit under the cap), but reliably keeps a
    /// random generation from ending up over the game's known-unsafe
    /// province limit. Used by the Random Generation tab's "cap at 1000
    /// provinces" checkbox (Runde 7, dritte Rückmeldung).</summary>
    public void EnforceProvinceCap(int maxCount)
    {
        if (ProvinceResult == null) return;
        int threshold = Constants.MinProvincePixels;
        bool everMerged = false;
        for (int round = 0; round < 24 && TotalProvinceCount() > maxCount; round++)
        {
            int before = TotalProvinceCount();
            ProvinceMapGen.MergeTinyProvinces(ProvinceResult.Labels, ProvinceResult.Provinces, threshold);
            if (TotalProvinceCount() != before) everMerged = true;
            threshold = (int)Math.Ceiling(threshold * 1.6) + 1;
        }
        if (everMerged) ProvinceMapGen.Recolor(ProvinceResult, Metadata.EmptyColor);
    }

    /// <summary>Full random-tile generation options (see GenerateRandom).</summary>
    public sealed class RandomTileOptions
    {
        /// <summary>0-100. Everything not chosen as land ends up sea/lake,
        /// exactly like the coastline brush would leave it.</summary>
        public double WaterPercent = 55.0;
        /// <summary>Roughly how many land provinces the automatic province
        /// generator is asked to aim for afterwards - not an exact
        /// guarantee (Voronoi seed placement always has some spread), but
        /// close in practice.</summary>
        public int TargetLandProvinces = 200;
        /// <summary>1 = a single continuous landmass (what "IsContinent"
        /// used to mean); higher values keep that many separate largest
        /// landmasses instead (an archipelago), discarding smaller
        /// leftover specks either way.</summary>
        public int IslandCount = 1;
        /// <summary>1 = fairly realistic, gently fractal coastlines; 10 =
        /// deliberately weird/obscure shapes (more octaves, higher-frequency
        /// detail, and domain-warped coordinates once past the midpoint).</summary>
        public int ObscurityLevel = 3;
        public int? Seed;

        /// <summary>When true (default), a thin ring of water is forced
        /// around every tile edge that isn't a chosen EdgeMode boundary, so
        /// the tile always reads as fully surrounded by sea/lake the way
        /// most interior RNW tiles are. Turn off for a tile meant to butt
        /// directly up against neighboring land with no strip of water in
        /// between.</summary>
        public bool WaterAlwaysSurroundsTile = true;

        public enum EdgeRestriction { None, North, South }
        /// <summary>Matches the existing restrict_to_north_edge /
        /// restrict_to_south_edge tile flags (see TileMetadata): the
        /// chosen edge is treated as a hard boundary of the whole Random
        /// New World map rather than a seam that blends into a neighboring
        /// tile, so it is generated mostly dry (a real map edge, not open
        /// water) and is exempt from the WaterAlwaysSurroundsTile ring.
        /// Implemented as a best-effort interpretation (no real
        /// north/south-edge RNW tile was available to check this against)
        /// - also sets the matching Metadata flag.</summary>
        public EdgeRestriction EdgeMode = EdgeRestriction.None;

        /// <summary>0-1. How mountainous/how high the tile reads overall:
        /// higher values grow both the fraction of land classified as
        /// mountainous and how much extra height (MountainBoost) it gets.</summary>
        public double MountainAmount = 0.35;

        /// <summary>When true, an automatic hydrologically-plausible river
        /// network (see RiverMapGen.AutoGenerateRivers) is generated after
        /// the height map, sourced from the highest mountain points and
        /// flowing downhill into the sea/lakes.</summary>
        public bool GenerateRivers = false;
        /// <summary>How many source rivers to attempt (actual count may be
        /// lower - a walk that never reaches water is discarded).</summary>
        public int RiverCount = 6;
        /// <summary>0-1. See RiverMapGen.AutoGenerateRivers - independent
        /// per-candidate-junction chance of a tributary joining a main
        /// river. 0 disables tributaries entirely.</summary>
        public double TributaryFrequency = 0.0;
        /// <summary>0-1. Same as TributaryFrequency, for distributaries
        /// (a branch splitting OFF a main river) instead.</summary>
        public double DistributaryFrequency = 0.0;
        /// <summary>0-1. See RiverMapGen.AutoGenerateRivers'
        /// minLengthFraction - a walk shorter than this fraction of the
        /// tile's own scale is discarded and another source is tried
        /// instead. 0 (default) accepts any length, matching the
        /// pre-Runde-21 behavior.</summary>
        public double MinRiverLength = 0.0;

        /// <summary>4-10. Cell size in pixels of the coarse river grid (see
        /// RiverMapGen.AutoGenerateRivers / CoarseRiverGen). Higher = calmer, smoother
        /// rivers and fewer small tributaries. Values outside 4-10 are clamped.</summary>
        public int RiverCellSize = CoarseRiverGen.DefaultCellSize;

        /// <summary>0-1. Adds a secondary, higher-frequency noise pass
        /// confined to a band around the coastline, producing more bays,
        /// inlets, and small peninsulas the higher this is. 0 leaves the
        /// coastline exactly as the base noise field produced it.</summary>
        public double CoastlineDetail = 0.25;

        /// <summary>When true, any inland sea/lake big enough to plausibly
        /// matter gets a carved water channel to the nearest open ocean
        /// (so ships can actually reach it), plus a best-effort Strait
        /// metadata entry across that channel as a fallback in case the
        /// carved width alone isn't enough for the game to treat the two
        /// sides as adjacent - this second part is an untested guess at
        /// how far to lean on the game's strait mechanism, flagged here
        /// the same way the boundary-bias heuristic was flagged earlier.</summary>
        public bool ConnectInlandSeas = true;

        /// <summary>0-1. How many small, isolated inland water pockets are
        /// allowed to survive as lakes rather than being filled back into
        /// land: 1 keeps every enclosed pocket found by the coastline noise
        /// (the original, unfiltered behavior), 0 fills almost everything
        /// up to a generous size cap back to land, leaving only the
        /// handful of genuinely large inland water bodies. Independent of
        /// ConnectInlandSeas, which only carves a channel for the few
        /// BIGGEST inland seas rather than reducing how many separate
        /// small ones exist in the first place. Default lowered (Runde 7,
        /// vierte Rückmeldung: "sollte es allgemein nicht so viele Seen
        /// geben") since many small lakes each become their own Lake
        /// province, eating into the tile's province budget for something
        /// with little gameplay value - see also
        /// ProvinceMapGen.MergeAllOfKind, which additionally consolidates
        /// whatever lakes remain into a single province.</summary>
        public double LakeFrequency = 0.3;

        /// <summary>0-1 chance, evaluated at every detected natural
        /// chokepoint (a narrow strip of sea with land close on two
        /// roughly-opposite sides), of registering a Strait connecting the
        /// two land provinces through the sea province there - on top of
        /// the always-on carved-inland-sea straits above. 0 disables this
        /// entirely. A Strait represents a land connection the game treats
        /// as existing DESPITE the two provinces not actually touching
        /// (separated by water) - a deliberate exception to how naval
        /// invasions normally work, meant for close island chains and
        /// similar cases (corrected understanding, Runde 7 - an earlier
        /// version of this generator had the gameplay logic backwards).</summary>
        public double StraitFrequency = 0.0;
        /// <summary>Straits are meant for CLOSE land, not distant crossings
        /// - a candidate chokepoint is only considered if the water gap
        /// between the two land provinces is at most this many pixels
        /// (Runde 7: user-defined; raised from the first round's 100px
        /// default to 200px after user testing showed straits were being
        /// rejected far too often).</summary>
        public double StraitMaxDistancePx = 200.0;
        /// <summary>Minimum distance (px) a new Strait's crossing point must
        /// keep from every already-registered Strait (including ones from
        /// earlier in the same generation pass) - avoids a cluster of
        /// crisscrossing connections all along the same stretch of coast
        /// (Runde 7 feedback). 0 disables the spacing check entirely.
        /// Raised from 15px to 48px for the same reason as
        /// StraitMaxDistancePx above.</summary>
        public double StraitMinSpacingPx = 48.0;
        /// <summary>How many Strait connections a single island (a
        /// connected landmass, which may span several provinces - not an
        /// individual province) may accumulate in total across both this
        /// auto-generation pass and any already-registered straits, before
        /// every further candidate touching it is skipped regardless of
        /// frequency/distance/spacing. Without a cap, a small, tightly
        /// packed island chain could otherwise accumulate a strait to every
        /// single nearby neighbor (Runde 7 feedback).</summary>
        public int StraitMaxPerIsland = 3;

        /// <summary>0-1 chance, evaluated per land province, of getting an
        /// automatically-picked flavor modifier (river_estuary_modifier at
        /// a river mouth, important_natural_harbor if coastal, a small
        /// chance of paradise_modifier) - 0 disables this entirely.</summary>
        public double ModifierFrequency = 0.0;

        /// <summary>How many region-seed entries (see TileMetadata.Regions
        /// - the same thing "Add as region seed" on the Provinces stage
        /// adds by hand) to place automatically, spread out across the
        /// generated land. 0 disables this entirely.</summary>
        public int AutoRegionCount = 0;
    }

    /// <summary>
    /// Generates an entire tile from scratch in one call: coastline (via
    /// fractal noise, thresholded to hit the requested water percentage),
    /// mountains (a second, higher-frequency noise field restricted to
    /// land and biased away from the immediate coast), a height map, and
    /// an automatically-generated province layout aimed at the requested
    /// land province count. Replaces LandMask/MountainMask/Height/
    /// RiverSegments/ProvinceResult entirely - meant for starting a tile,
    /// not for touching one already in progress (the individual stages
    /// remain available afterwards for manual fine-tuning, exactly as the
    /// tile-making guide expects).
    /// </summary>
    public void GenerateRandom(RandomTileOptions options)
    {
        int seed = options.Seed ?? Environment.TickCount;
        // Also drives the province layer's own RNG (GenerateProvincesAuto
        // reads Settings.RandomSeed) so the same seed reproduces the whole
        // tile - coastline, mountains, AND provinces - not just the parts
        // generated directly in this method.
        Settings.RandomSeed = seed;
        int w = WidthPx, h = HeightPx;
        int obscurity = Math.Clamp(options.ObscurityLevel, 1, 10);
        double t = (obscurity - 1) / 9.0; // 0..1

        // Low obscurity: few octaves, low persistence, large smooth
        // features, no domain warp - close to a real, gently fractal
        // coastline. High obscurity: more octaves, higher persistence
        // (rougher detail retained at small scales), a smaller base scale
        // (more, smaller landmass "blobs"), and domain warping kicks in
        // past the midpoint for genuinely strange, non-physical shapes.
        int octaves = 2 + (int)Math.Round(t * 4);           // 2..6
        double persistence = 0.35 + t * 0.30;                // 0.35..0.65
        double baseScale = Math.Max(w, h) / (5.5 - t * 3.0); // bigger tile-fraction .. smaller/busier
        double warpStrength = t > 0.5 ? (t - 0.5) * 2.0 * baseScale * 0.5 : 0;

        var field = NoiseGen.GenerateField(w, h, baseScale, octaves, persistence, 2.0, seed, warpStrength);

        // -- water-always-surrounds-tile: push the field down near every
        // edge that will be forced to water, BEFORE thresholding into
        // land/sea, so the coastline naturally recedes and curves away
        // from the edge over a wide-ish margin instead of running flat
        // into it and then getting sliced by a straight forced-water
        // strip (which used to read as an unnatural, obviously "cut off"
        // edge - every province touching it ended in a dead straight
        // line). The literal edge pixels are still additionally forced to
        // water further below as a guarantee, but by then there is
        // already open water well before it in the vast majority of
        // cases, so that forced strip rarely has to cut through anything.
        //
        // Runde 20 fix: this recede push used to be SKIPPED entirely for
        // the restricted north/south edge (Runde 19), on the theory that
        // leaving it alone would let the coastline "decide naturally".
        // Reference province maps of real north/south-edge tiles the user
        // provided (mostly deep, open water spanning the whole edge, with
        // only the odd organic peninsula poking through) show that's
        // wrong: without ANY recede bias, whatever the broad-scale noise
        // put there just runs edge-to-edge, and since a tile is only ever
        // a small slice of a much bigger virtual landmass, that's at
        // least as likely to be solid land as solid water - which is
        // exactly the "seltsame Linie" still being reported (a dead flat
        // land/wasteland cut spanning nearly the whole width). Applying
        // the SAME recede-toward-water push here as every other edge
        // fixes that; what actually distinguishes a restricted edge from
        // an ordinary one is handled below, by exempting it alone from
        // the guaranteed water RING (so the rare peninsula the recede
        // push still allows through can actually touch the true edge,
        // instead of being clipped back to water like on every other side).
        if (options.WaterAlwaysSurroundsTile)
        {
            int marginPx = Math.Max(24, (int)(Math.Min(w, h) * 0.06));
            for (int y = 0; y < h; y++)
            {
                double distN = y;
                double distS = h - 1 - y;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    double distW = x, distE = w - 1 - x;
                    double dist = Math.Min(Math.Min(distN, distS), Math.Min(distW, distE));
                    if (dist >= marginPx) continue;
                    double t3 = 1.0 - dist / marginPx; // 1 right at the edge, 0 at the margin's inner boundary
                    double falloff = t3 * t3 * (3 - 2 * t3); // smoothstep - gentle near the inner edge, strong right at the border
                    field[row + x] -= (float)(falloff * 3.2); // noise is roughly [-1,1]; this reliably pushes below any realistic land threshold right at the edge
                }
            }
        }

        // -- restricted edge: a much wider, deep-open-ocean-style push on
        // TOP of the generic margin above, applied only to the chosen
        // north/south edge (Runde 20, from the user's reference province
        // maps: real north/south-edge tiles read as mostly deep water
        // across nearly the whole edge, with only the odd organic
        // peninsula reaching it - the generic 6%-of-tile margin above is
        // far too narrow to produce that against a landmass whose natural
        // extent, at this noise scale, is often bigger than the tile
        // itself). Wide enough that only the strongest local noise peaks
        // still punch land through it.
        if (options.EdgeMode != RandomTileOptions.EdgeRestriction.None)
        {
            bool north = options.EdgeMode == RandomTileOptions.EdgeRestriction.North;
            int deepMarginPx = Math.Max(40, (int)(Math.Min(w, h) * 0.32));
            for (int y = 0; y < Math.Min(deepMarginPx, h); y++)
            {
                // y is already edge-relative distance by construction (0 =
                // right at the restricted edge) - row is the only thing
                // that differs between north (row=y) and south (row=h-1-y).
                double t4 = 1.0 - y / (double)deepMarginPx;
                double falloff = t4 * t4 * (3 - 2 * t4);
                int row = (north ? y : h - 1 - y) * w;
                for (int x = 0; x < w; x++)
                    field[row + x] -= (float)(falloff * 7.0);
            }
        }

        // Percentile threshold over the *tile-shaped* pixels only (this
        // project's tiles are usually rectangular, but LoadExistingTile
        // can leave an irregular EmptyMask - never turn those into land).
        var sorted = new List<float>(w * h);
        for (int i = 0; i < field.Length; i++)
            if (EmptyMask.Data[i] < 128) sorted.Add(field[i]);
        var land = GrayMap.Bool(w, h, false);
        if (sorted.Count > 0)
        {
            sorted.Sort();
            double landFraction = Math.Clamp(1.0 - options.WaterPercent / 100.0, 0.01, 0.99);
            int thresholdIdx = Math.Clamp((int)((1.0 - landFraction) * sorted.Count), 0, sorted.Count - 1);
            float threshold = sorted[thresholdIdx];
            for (int i = 0; i < field.Length; i++)
                if (EmptyMask.Data[i] < 128 && field[i] >= threshold) land.Data[i] = 255;
        }
        // -- coastline detail: a secondary, higher-frequency noise pass
        // confined to a band around the current coastline, so higher
        // values read as more bays/inlets/small peninsulas rather than a
        // globally rougher landmass shape (that's what ObscurityLevel
        // already does).
        if (options.CoastlineDetail > 0.01)
        {
            var toSea = RasterOps.DistanceFrom(GrayMap.Not(land));
            var toLand = RasterOps.DistanceFrom(land);
            double bandWidth = Math.Max(6.0, Math.Max(w, h) * 0.025 * (0.4 + options.CoastlineDetail));
            var detailField = NoiseGen.GenerateField(w, h, Math.Max(8.0, baseScale * 0.05), 3, 0.55, 2.0, seed + 7777);
            double detailStrength = Math.Clamp(options.CoastlineDetail, 0.0, 1.0);
            for (int i = 0; i < land.Data.Length; i++)
            {
                if (EmptyMask.Data[i] >= 128) continue;
                bool isLand = land.Data[i] >= 128;
                double distToOpposite = isLand ? toSea[i] : toLand[i];
                if (distToOpposite > bandWidth) continue;
                double edgeT = 1.0 - distToOpposite / bandWidth; // 1 right on the coastline, 0 at the band's outer edge
                double flipChance = detailStrength * edgeT;
                if (Math.Abs(detailField[i]) < flipChance)
                    land.Data[i] = detailField[i] > 0 ? (byte)255 : (byte)0;
            }
        }

        land = RasterOps.LargestComponents(land, options.IslandCount);

        // -- edge-restriction tile (Runde 19/20 fix): the previous version
        // force-flipped almost the whole margin along the chosen edge to
        // land, which read as an obviously artificial straight line (user
        // feedback). The restricted edge now gets the exact same
        // water-recede push as every other edge (see above), so the
        // coastline organically curves away from it in the vast majority
        // of cases just like a normal tile border; what makes it
        // different is that it alone is exempt from the guaranteed water
        // RING further below, so on the rare occasion land does reach
        // that far, it's allowed to actually touch the true edge - an
        // organic peninsula, not a manufactured straight cut - matching
        // real north/south-edge tiles (mostly open water, the odd
        // peninsula poking through). Where land DOES reach that edge it
        // gets hugged with a thin Wasteland margin further below, once
        // WastelandMask exists.
        Metadata.RestrictToNorthEdge = options.EdgeMode == RandomTileOptions.EdgeRestriction.North;
        Metadata.RestrictToSouthEdge = options.EdgeMode == RandomTileOptions.EdgeRestriction.South;

        // -- final thin safety-net ring: the pre-threshold margin fade
        // above already makes the coastline recede naturally well before
        // the literal edge in the vast majority of cases, but a few
        // things can still put land right up against the border (a
        // largest-island pick that happens to touch it, coastline-detail
        // noise flipping an edge pixel back to land, ...), so this keeps
        // the hard guarantee that the tile reads as fully surrounded by
        // sea, same as before - it should now rarely have to visibly cut
        // through anything, since the organic recession already happened.
        if (options.WaterAlwaysSurroundsTile)
        {
            int ring = Math.Max(3, (int)(Math.Min(w, h) * 0.012));
            bool skipNorth = options.EdgeMode == RandomTileOptions.EdgeRestriction.North;
            bool skipSouth = options.EdgeMode == RandomTileOptions.EdgeRestriction.South;
            for (int y = 0; y < h; y++)
            {
                bool nearNorth = y < ring, nearSouth = y >= h - ring;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (EmptyMask.Data[row + x] >= 128) continue;
                    bool nearWest = x < ring, nearEast = x >= w - ring;
                    bool onForcedEdge = (nearNorth && !skipNorth) || (nearSouth && !skipSouth) || nearWest || nearEast;
                    if (onForcedEdge) land.Data[row + x] = 0;
                }
            }
        }

        LandMask = land;
        Array.Clear(WastelandMask.Data, 0, WastelandMask.Data.Length);
        if (options.EdgeMode != RandomTileOptions.EdgeRestriction.None)
        {
            bool north = options.EdgeMode == RandomTileOptions.EdgeRestriction.North;
            int wasteMargin = Math.Max(4, (int)(Math.Min(w, h) * 0.015));
            for (int y = 0; y < h; y++)
            {
                int distToEdge = north ? y : (h - 1 - y);
                if (distToEdge >= wasteMargin) continue;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (land.Data[row + x] >= 128) WastelandMask.Data[row + x] = 255;
                }
            }
        }
        RiverSegments.Clear();
        ImportedRiverRaster = null;

        if (options.ConnectInlandSeas) ConnectInlandSeasToOpenOcean(land);
        // Runs AFTER ConnectInlandSeasToOpenOcean so the few biggest
        // inland seas it already turned into channel-connected open water
        // are excluded from this pass entirely - this only ever fills in
        // whatever smaller, still-isolated water pockets remain.
        FillSmallLakes(land, options.LakeFrequency);

        // Mountains: a second, higher-frequency ridge field, restricted to
        // land and faded out right at the coast (distance-from-sea < the
        // land rise distance) so peaks don't sit directly on the shoreline.
        var ridge = NoiseGen.GenerateField(w, h, baseScale * 0.28, Math.Min(octaves + 1, 6), persistence, 2.0, seed + 55555, warpStrength * 0.5);
        float[] distIntoLand = RasterOps.DistanceFrom(GrayMap.Not(land));
        var landRidgeValues = new List<float>();
        for (int i = 0; i < ridge.Length; i++)
            if (land.Data[i] >= 128) landRidgeValues.Add(ridge[i]);

        double mountainAmount = Math.Clamp(options.MountainAmount, 0.0, 1.0);

        MountainMask = new GrayMap(w, h);
        if (landRidgeValues.Count > 0)
        {
            landRidgeValues.Sort();
            // Higher obscurity nudges this a little too, but MountainAmount
            // is now the primary dial for "how mountainous/how high".
            double mtnFraction = Math.Clamp(0.03 + mountainAmount * 0.32 + t * 0.06, 0.01, 0.6);
            int idx = Math.Clamp((int)((1.0 - mtnFraction) * landRidgeValues.Count), 0, landRidgeValues.Count - 1);
            float mtnThreshold = landRidgeValues[idx];
            float span = Math.Max(landRidgeValues[^1] - mtnThreshold, 1e-4f);
            double coastFade = Math.Max(Settings.LandRiseDistance, 1.0);
            for (int i = 0; i < ridge.Length; i++)
            {
                if (land.Data[i] < 128 || ridge[i] < mtnThreshold) continue;
                double intensity = (ridge[i] - mtnThreshold) / span;
                double coastT = Math.Clamp(distIntoLand[i] / coastFade, 0.0, 1.0);
                MountainMask.Data[i] = (byte)Math.Clamp(Math.Round(intensity * coastT * 255.0), 0, 255);
            }
        }

        // MountainAmount also drives how much extra height the mountain
        // brush/generator adds and how tall the interior generally reads,
        // so "how mountainous/how high" reads as one consistent dial
        // rather than only changing which fraction of land counts as
        // mountainous.
        Settings.MountainBoost = 40.0 + mountainAmount * 110.0;
        Settings.LandInteriorHeight = 120.0 + mountainAmount * 70.0;

        GenerateHeight();

        if (options.GenerateRivers && options.RiverCount > 0)
        {
            var riverRng = new Random(seed + 424242);
            RiverSegments.AddRange(RiverMapGen.AutoGenerateRivers(LandMask, Height!, options.RiverCount, riverRng, options.TributaryFrequency, options.DistributaryFrequency, options.MinRiverLength, options.RiverCellSize));
        }

        double cellArea = (double)Constants.GridUnit * Constants.GridUnit;
        double landCells = Math.Max(land.CountTrue() / cellArea, 0.01);
        Settings.LandDensityPerCell = Math.Clamp(options.TargetLandProvinces / landCells, 0.1, 40);
        Settings.WastelandDensityPerCell = 0.1; // random generation doesn't place wasteland - paint/reclassify it afterwards if wanted
        // Same obscurity dial drives both the coastline shape and the
        // land province boundary irregularity, so "obscure" reads as one
        // consistent style rather than a weird coastline with tidy,
        // regular-grid provinces (or vice versa).
        Settings.LandObscurity = obscurity;
        GenerateProvincesAuto();

        if (options.ConnectInlandSeas) AddStraitsForCarvedChannels();

        var flavorRng = new Random(seed + 909090);
        if (options.StraitFrequency > 0.001) AutoGenerateNaturalStraits(options, flavorRng);
        if (options.ModifierFrequency > 0.001) AutoGenerateModifiers(options.ModifierFrequency, flavorRng);
        if (options.AutoRegionCount > 0) AutoGenerateRegions(options.AutoRegionCount, flavorRng);
    }

    /// <summary>Finds the first land province reached walking from (x0,y0)
    /// in direction (dx,dy) (not necessarily unit length - this
    /// normalizes), up to maxReach pixels, together with how many pixels
    /// away it was found - or null if water/tile-edge is hit first without
    /// finding one.</summary>
    private (int pid, int steps)? FindLandProvinceInDirection(int x0, int y0, double dx, double dy, int maxReach)
    {
        if (ProvinceResult == null) return null;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return null;
        dx /= len; dy /= len;
        var labels = ProvinceResult.Labels;
        int w = labels.Width, h = labels.Height;
        for (int step = 1; step <= maxReach; step++)
        {
            int x = (int)Math.Round(x0 + dx * step), y = (int)Math.Round(y0 + dy * step);
            if (x < 0 || x >= w || y < 0 || y >= h) return null;
            int pid = labels.Data[y * w + x];
            if (pid < 0) continue;
            if (!ProvinceResult.Provinces.TryGetValue(pid, out var info)) continue;
            if (info.Kind == ProvinceKind.Land) return (pid, step);
            if (info.Kind != ProvinceKind.Sea) return null; // hit wasteland/lake/empty - not a clean strait candidate
        }
        return null;
    }

    /// <summary>Scans every narrow sea chokepoint (land close on two
    /// roughly-opposite sides) on a coarse grid and, with probability
    /// `frequency` per candidate, registers a Strait connecting the two
    /// land provinces through the sea province at that point.
    ///
    /// A Strait represents a land connection the game treats as existing
    /// even though the two provinces do NOT actually touch (they are
    /// separated by water) - the game's own way of letting an island chain
    /// or a tightly-packed group of islands/coastal provinces be invaded
    /// over "land" despite naval invasions normally being harder (corrected
    /// understanding, Runde 7 - do not reintroduce the "does skipping this
    /// save a long detour" framing an earlier version of this generator
    /// used, that was based on a wrong read of the mechanic). Because of
    /// that, straits should stay CLOSE (StraitMaxDistancePx) and SPARSE
    /// (StraitMinSpacingPx keeps a new one away from any already-registered
    /// crossing) - lots of long-range or crisscrossing straits reads as
    /// noise, not a deliberate island chain. StraitMaxPerIsland additionally
    /// caps how many connections any one island can accumulate in total, so
    /// a small tightly-packed island group can't end up strung to every
    /// single neighbor (Runde 7 feedback). Public (not just used by
    /// GenerateRandom) so the Special Features stage can also run this
    /// on-demand on an already-generated tile, with its own settings.</summary>
    public void AutoGenerateNaturalStraits(RandomTileOptions options, Random rng)
    {
        if (ProvinceResult == null) return;
        double frequency = options.StraitFrequency;
        double maxDistancePx = Math.Max(4, options.StraitMaxDistancePx);
        double minSpacingPx = Math.Max(0, options.StraitMinSpacingPx);
        int maxPerIsland = Math.Max(1, options.StraitMaxPerIsland);
        int w = WidthPx, h = HeightPx;
        var labels = ProvinceResult.Labels;
        var distToLand = RasterOps.DistanceFrom(LandMask);
        var alreadyConnected = new HashSet<(int a, int b)>();
        foreach (var s in Metadata.Straits) alreadyConnected.Add(OrderedPair(ColorId(s.From), ColorId(s.To)));

        // Islands (connected landmasses, which may span several provinces)
        // for the per-island connection cap - deliberately NOT per-province,
        // since the user's own framing ("eine Insel darf maximal X Strait
        // Zugänge haben") is about the landmass as a whole, not any one
        // province on it.
        var (islandLabels, islandCount) = RasterOps.LabelRegions(LandMask, GrayMap.Bool(w, h, false));
        int IslandOf(ProvinceInfo info) => islandLabels[info.Seed.y * w + info.Seed.x];
        var islandStraitCount = new int[islandCount + 1];
        void CountIsland(int island) { if (island > 0) islandStraitCount[island]++; }

        // Existing strait crossing points (for the min-spacing rule),
        // approximated by the "through" sea province's own seed point -
        // Metadata.Straits only stores colors, not coordinates.
        var colorToId = new Dictionary<Rgb, int>();
        foreach (var kv in ProvinceResult.Provinces) colorToId[kv.Value.Color] = kv.Key;
        var existingPositions = new List<(double x, double y)>();
        foreach (var s in Metadata.Straits)
        {
            if (colorToId.TryGetValue(s.Through, out var throughPid) && ProvinceResult.Provinces.TryGetValue(throughPid, out var throughInfo))
                existingPositions.Add(throughInfo.Seed);
            if (colorToId.TryGetValue(s.From, out var fromPid) && ProvinceResult.Provinces.TryGetValue(fromPid, out var fromInfo)) CountIsland(IslandOf(fromInfo));
            if (colorToId.TryGetValue(s.To, out var toPid) && ProvinceResult.Provinces.TryGetValue(toPid, out var toInfo)) CountIsland(IslandOf(toInfo));
        }

        bool TooCloseToExisting(double x, double y)
        {
            if (minSpacingPx <= 0) return false;
            foreach (var (ex, ey) in existingPositions)
            {
                double ddx = x - ex, ddy = y - ey;
                if (ddx * ddx + ddy * ddy < minSpacingPx * minSpacingPx) return true;
            }
            return false;
        }

        (int dx, int dy)[] axes = { (1, 0), (0, 1), (1, 1), (1, -1) };
        int step = Math.Max(6, Math.Min(w, h) / 140);
        // Each direction only ever needs to search half the allowed total
        // gap - the two sides' step counts are added below to get the
        // actual crossing distance.
        int reachPerSide = Math.Max(4, (int)Math.Round(maxDistancePx / 2.0));

        for (int y = step; y < h - step; y += step)
        {
            int row = y * w;
            for (int x = step; x < w - step; x += step)
            {
                int idx = row + x;
                if (LandMask.Data[idx] >= 128) continue; // candidate point must itself be water
                if (distToLand[idx] > reachPerSide) continue; // too far from land on every side to matter

                int seaPid = labels.Data[idx];
                if (seaPid < 0 || !ProvinceResult.Provinces.TryGetValue(seaPid, out var seaInfo) || seaInfo.Kind != ProvinceKind.Sea) continue;
                if (TooCloseToExisting(x, y)) continue;

                foreach (var (dx, dy) in axes)
                {
                    var aHit = FindLandProvinceInDirection(x, y, dx, dy, reachPerSide);
                    var bHit = FindLandProvinceInDirection(x, y, -dx, -dy, reachPerSide);
                    if (aHit == null || bHit == null || aHit.Value.pid == bHit.Value.pid) continue;
                    if (aHit.Value.steps + bHit.Value.steps > maxDistancePx) continue;
                    if (!ProvinceResult.Provinces.TryGetValue(aHit.Value.pid, out var ainfo)) continue;
                    if (!ProvinceResult.Provinces.TryGetValue(bHit.Value.pid, out var binfo)) continue;
                    int islandA = IslandOf(ainfo), islandB = IslandOf(binfo);
                    if (islandA > 0 && islandStraitCount[islandA] >= maxPerIsland) continue;
                    if (islandB > 0 && islandStraitCount[islandB] >= maxPerIsland) continue;
                    // Dedup key must live in the same space as the one
                    // alreadyConnected was seeded with above (province
                    // COLOR, not province id - ids aren't known yet for the
                    // pre-existing Metadata.Straits entries scanned above,
                    // only their From/To colors are) - otherwise this check
                    // silently never matches anything already registered.
                    var key = OrderedPair(ColorId(ainfo.Color), ColorId(binfo.Color));
                    if (alreadyConnected.Contains(key)) continue;
                    if (rng.NextDouble() > frequency) continue;
                    alreadyConnected.Add(key);
                    existingPositions.Add((x, y));
                    CountIsland(islandA);
                    CountIsland(islandB);
                    Metadata.Straits.Add(new Strait { From = ainfo.Color, To = binfo.Color, Through = seaInfo.Color });
                    break; // one strait registered for this chokepoint is enough
                }
            }
        }
    }

    private static (int, int) OrderedPair(int a, int b) => a < b ? (a, b) : (b, a);
    private static int ColorId(Rgb c) => (c.R << 16) | (c.G << 8) | c.B;

    /// <summary>Per land province, with probability `frequency`, assigns an
    /// automatically-picked flavor modifier: river_estuary_modifier at a
    /// river mouth (always, when there is one); otherwise a weighted pick
    /// among important_natural_harbor (coastal only), level_1_center_of_trade
    /// (anywhere - trade nodes aren't tied to a coastline), and
    /// paradise_modifier, which is deliberately the rarest of the three by a
    /// wide margin (Runde 6 feedback: a "paradise" should be an occasional
    /// find, not a common one, and plain trade centers were missing
    /// entirely from the old logic). `frequency` alone controls how MANY
    /// provinces get any modifier at all; which one they get is then this
    /// fixed relative weighting, independent of the slider. See
    /// RandomTileOptions.ModifierFrequency.</summary>
    public void AutoGenerateModifiers(double frequency, Random rng)
    {
        if (ProvinceResult == null) return;
        int w = WidthPx, h = HeightPx;
        var labels = ProvinceResult.Labels;

        var riverMouthPids = new HashSet<int>();
        foreach (var (mx, my) in RiverMapGen.RiverMouths(LandMask, RiverSegments))
        {
            for (int r = 0; r <= 4; r++)
            {
                bool found = false;
                for (int dy = -r; dy <= r && !found; dy++)
                {
                    for (int dx = -r; dx <= r && !found; dx++)
                    {
                        int x = mx + dx, y = my + dy;
                        if (x < 0 || x >= w || y < 0 || y >= h) continue;
                        int pid = labels.Data[y * w + x];
                        if (pid < 0 || !ProvinceResult.Provinces.TryGetValue(pid, out var info) || info.Kind != ProvinceKind.Land) continue;
                        riverMouthPids.Add(pid);
                        found = true;
                    }
                }
                if (found) break;
            }
        }

        foreach (var (pid, info) in ProvinceResult.Provinces)
        {
            if (info.Kind != ProvinceKind.Land) continue;
            if (rng.NextDouble() > frequency) continue;

            string? modName;
            if (riverMouthPids.Contains(pid))
            {
                modName = "river_estuary_modifier";
            }
            else
            {
                var (sx, sy) = info.Seed;
                // Cheap coastal check anchored on the province's own seed
                // point is good enough here (flavor, not a hard rule) - a
                // proper per-pixel scan isn't worth the cost for what's
                // already a probabilistic feature.
                bool coastal = IsNearWater(sx, sy, 40);

                var choices = new List<(string name, double weight)> { ("level_1_center_of_trade", 0.55) };
                if (coastal) choices.Add(("important_natural_harbor", 0.55));
                choices.Add(("paradise_modifier", 0.05)); // clearly the rarest of the three

                double total = 0;
                foreach (var c in choices) total += c.weight;
                double roll = rng.NextDouble() * total;
                modName = choices[^1].name; // fallback for float rounding at the very top of the range
                foreach (var c in choices)
                {
                    if (roll < c.weight) { modName = c.name; break; }
                    roll -= c.weight;
                }
            }

            if (!Metadata.Modifiers.TryGetValue(modName, out var list)) Metadata.Modifiers[modName] = list = new List<Rgb>();
            if (!list.Contains(info.Color)) list.Add(info.Color);
        }
    }

    private bool IsNearWater(int x0, int y0, int radius)
    {
        int w = WidthPx, h = HeightPx;
        for (int dy = -radius; dy <= radius; dy += 4)
        {
            int y = y0 + dy;
            if (y < 0 || y >= h) continue;
            for (int dx = -radius; dx <= radius; dx += 4)
            {
                int x = x0 + dx;
                if (x < 0 || x >= w) continue;
                if (LandMask.Data[y * w + x] < 128) return true;
            }
        }
        return false;
    }

    /// <summary>Places `count` region-seed entries (TileMetadata.Regions -
    /// exactly what "Add as region seed" on the Provinces stage adds by
    /// hand for one clicked province), spread across the generated land by
    /// simple rejection sampling on each candidate's seed point so they
    /// don't cluster together. See RandomTileOptions.AutoRegionCount.</summary>
    public void AutoGenerateRegions(int count, Random rng)
    {
        if (ProvinceResult == null) return;
        var allLand = new List<ProvinceInfo>();
        foreach (var info in ProvinceResult.Provinces.Values)
            if (info.Kind == ProvinceKind.Land) allLand.Add(info);
        if (allLand.Count == 0) return;

        // A region entry names ONE representative province by color for the
        // game to compute a "region center point" from (see the game's own
        // "Failed to generate region center point" error, Runde 23 - a real
        // exported tile hit this for 5 of 6 regions). Unfiltered, this could
        // pick any land province including a tiny 1-2px sliver left over
        // from border-curviness/random generation - untested against the
        // real game (no local EU4 install to verify against), but a
        // near-zero-area province is a plausible reason a center-point
        // calculation would fail, and it is a poor "region seed" regardless.
        // Restrict candidates to reasonably-sized provinces (at least a
        // quarter of the mean land province size on this tile).
        double meanPixels = allLand.Average(p => (double)p.PixelCount);
        double minPixels = meanPixels * 0.25;
        var landProvinces = allLand.Where(p => p.PixelCount >= minPixels).ToList();
        if (landProvinces.Count == 0) landProvinces = allLand; // degenerate tile - fall back rather than place zero regions

        double minSep = Math.Sqrt((double)WidthPx * HeightPx / Math.Max(count, 1)) * 0.6;
        var chosen = new List<(int x, int y)>();
        var shuffled = new List<ProvinceInfo>(landProvinces);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        foreach (var info in shuffled)
        {
            if (chosen.Count >= count) break;
            var (sx, sy) = info.Seed;
            bool tooClose = false;
            foreach (var (cx, cy) in chosen)
            {
                double dx = sx - cx, dy = sy - cy;
                if (dx * dx + dy * dy < minSep * minSep) { tooClose = true; break; }
            }
            if (tooClose) continue;
            chosen.Add((sx, sy));
            if (!Metadata.Regions.Contains(info.Color)) Metadata.Regions.Add(info.Color);
        }
    }

    /// <summary>Channels carved by ConnectInlandSeasToOpenOcean, kept only
    /// long enough (until right after province generation) to add the
    /// best-effort Strait metadata entries in AddStraitsForCarvedChannels -
    /// see RandomTileOptions.ConnectInlandSeas's doc comment for why this
    /// is flagged as an untested guess.</summary>
    private readonly List<List<(int x, int y)>> _pendingCarvedChannels = new();

    /// <summary>Finds every inland sea/lake big enough to plausibly matter
    /// (a few thousand pixels - well past a decorative pond) and carves a
    /// short water channel from it to the nearest already-open ocean
    /// through the shortest stretch of land, so the two water bodies end
    /// up pixel-adjacent (which is what actually makes the game treat them
    /// as navigably connected - the same physical requirement as any real
    /// coastline). Mutates `land` in place; queues the carved paths for
    /// AddStraitsForCarvedChannels to also register as Strait metadata
    /// once provinces exist.</summary>
    private void ConnectInlandSeasToOpenOcean(GrayMap land)
    {
        _pendingCarvedChannels.Clear();
        int w = land.Width, h = land.Height;
        const int minLakePixels = 2500; // roughly a 50x50px pond and up

        for (int attempt = 0; attempt < 8; attempt++) // a few lakes might exist; cap the number of carve passes
        {
            var water = GrayMap.Not(land);
            for (int i = 0; i < water.Data.Length; i++) if (EmptyMask.Data[i] >= 128) water.Data[i] = 0;
            var openSea = RasterOps.BorderConnected(water);
            var lakeMask = GrayMap.And(water, GrayMap.Not(openSea));
            if (!lakeMask.AnyTrue()) break;

            var (labels, n) = RasterOps.LabelRegions(lakeMask, GrayMap.Bool(w, h, false));
            if (n == 0) break;
            var counts = new int[n + 1];
            foreach (var l in labels) if (l > 0) counts[l]++;

            int biggest = 0;
            for (int i = 1; i <= n; i++) if (counts[i] > (biggest == 0 ? 0 : counts[biggest])) biggest = i;
            if (biggest == 0 || counts[biggest] < minLakePixels) break;

            var thisLake = GrayMap.Bool(w, h, false);
            for (int i = 0; i < labels.Length; i++) if (labels[i] == biggest) thisLake.Data[i] = 255;

            var path = RasterOps.ShortestPathThroughMask(thisLake, openSea, land);
            if (path == null || path.Count == 0) break; // unreachable (or nothing but water/land - shouldn't happen) - stop rather than loop forever

            foreach (var (x, y) in path)
                RasterOps.PaintBoolBrush(land, x, y, 1.5, false); // ~3px wide, wide enough to plausibly read as passable water

            _pendingCarvedChannels.Add(path);
            if (_pendingCarvedChannels.Count >= 5) break; // bound worst-case runtime on a tile with many small lakes
        }
    }

    /// <summary>Fills small enclosed (non-open-ocean) water pockets back
    /// into land, so fewer, smaller separate lake bodies end up on the
    /// tile (Runde 7, vierte Rückmeldung - see RandomTileOptions.
    /// LakeFrequency's doc comment). Deliberately size-threshold-based
    /// rather than probabilistic per pocket, so the result is
    /// deterministic for a given seed/frequency: at frequency 1 nothing is
    /// filled; at frequency 0, every pocket up to a generous cap (well
    /// past ConnectInlandSeasToOpenOcean's own "big enough to matter"
    /// threshold) is filled, leaving only genuinely large inland water
    /// bodies. Mutates `land` in place.</summary>
    private void FillSmallLakes(GrayMap land, double frequency)
    {
        int w = land.Width, h = land.Height;
        var water = GrayMap.Not(land);
        for (int i = 0; i < water.Data.Length; i++) if (EmptyMask.Data[i] >= 128) water.Data[i] = 0;
        var openSea = RasterOps.BorderConnected(water);
        var lakeMask = GrayMap.And(water, GrayMap.Not(openSea));
        if (!lakeMask.AnyTrue()) return;

        var (labels, n) = RasterOps.LabelRegions(lakeMask, GrayMap.Bool(w, h, false));
        if (n == 0) return;
        var counts = new int[n + 1];
        for (int i = 0; i < labels.Length; i++) if (labels[i] > 0) counts[labels[i]]++;

        const int fillCapPx = 6000; // comfortably above ConnectInlandSeasToOpenOcean's own 2500px "matters" threshold
        double freq = Math.Clamp(frequency, 0.0, 1.0);
        int fillThreshold = (int)Math.Round((1.0 - freq) * fillCapPx);
        if (fillThreshold <= 0) return;

        for (int i = 0; i < labels.Length; i++)
        {
            int lbl = labels[i];
            if (lbl > 0 && counts[lbl] <= fillThreshold) land.Data[i] = 255;
        }
    }

    /// <summary>Second half of ConnectInlandSeasToOpenOcean: once provinces
    /// exist, look up the land province on each side of every carved
    /// channel (near its two endpoints) and the sea province now covering
    /// the channel itself, and register a Strait between them - a
    /// best-effort fallback in case the physically carved width alone
    /// isn't enough for the game's own adjacency detection. See
    /// RandomTileOptions.ConnectInlandSeas's doc comment.</summary>
    private void AddStraitsForCarvedChannels()
    {
        if (ProvinceResult == null || _pendingCarvedChannels.Count == 0) return;
        var labels = ProvinceResult.Labels;
        int w = labels.Width, h = labels.Height;

        Rgb? LandColorNear(int x, int y, int radius)
        {
            for (int r = 1; r <= radius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int pid = labels.Data[ny * w + nx];
                        if (pid < 0 || !ProvinceResult.Provinces.TryGetValue(pid, out var info)) continue;
                        if (info.Kind == ProvinceKind.Land) return info.Color;
                    }
                }
            }
            return null;
        }

        foreach (var path in _pendingCarvedChannels)
        {
            if (path.Count == 0) continue;
            var (sx, sy) = path[0];
            var (ex, ey) = path[^1];
            var fromColor = LandColorNear(sx, sy, 8);
            var toColor = LandColorNear(ex, ey, 8);
            if (fromColor == null || toColor == null || fromColor.Value.Equals(toColor.Value)) continue;

            var (mx, my) = path[path.Count / 2];
            int midPid = labels.Data[Math.Clamp(my, 0, h - 1) * w + Math.Clamp(mx, 0, w - 1)];
            Rgb throughColor;
            if (midPid >= 0 && ProvinceResult.Provinces.TryGetValue(midPid, out var midInfo) && midInfo.Kind is ProvinceKind.Sea or ProvinceKind.Lake)
                throughColor = midInfo.Color;
            else
                continue; // couldn't confidently identify the water province - skip rather than record something wrong

            Metadata.Straits.Add(new Strait { From = fromColor.Value, To = toColor.Value, Through = throughColor });
        }
        _pendingCarvedChannels.Clear();
    }

    // -- importing an external, unfinished heightmap sketch --------------------

    public sealed class HeightmapImportOptions
    {
        /// <summary>0-255 byte threshold in the source grayscale heightmap:
        /// pixels at or above this become land, below become sea - the
        /// same "sea level" idea as any real heightmap-derived coastline.</summary>
        public int SeaLevel = 90;
        /// <summary>EdgeStrength threshold for detecting a sketched ridge
        /// line (see RasterOps.WidenAndBlurRidgeLines) - the heightmap
        /// this was built against was sketched with "20" in mind.</summary>
        public int EdgeThreshold = 20;
        /// <summary>How many pixels to thicken a detected line by before
        /// blurring it - bigger source images need a bigger radius here to
        /// end up with a believably wide mountain range.</summary>
        public int LineWidenRadius = 9;
        /// <summary>How far out from a detected line the blur is allowed
        /// to reach ("mostly affect the areas around the lines" - the
        /// reference comment this was built from).</summary>
        public int CorridorRadius = 20;
        public double BlurSigma = 12.0;
        /// <summary>0-255: land pixels ending up at or above this height
        /// after the widen/blur pass are seeded into MountainMask (scaled
        /// 0..255 by how far above the threshold they land), so the new
        /// ridges immediately show up as paintable/adjustable mountains
        /// rather than only being baked silently into the height field.</summary>
        public int MountainHeightThreshold = 150;
    }

    /// <summary>
    /// Import an external, roughly-sketched heightmap image as the
    /// starting point for a new tile: derives LandMask from a sea-level
    /// threshold, then widens/blurs any sketched ridge lines into real
    /// mountain ranges (RasterOps.WidenAndBlurRidgeLines) and seeds
    /// MountainMask from the result, then runs the normal height
    /// generator from that land/mountain starting point (exactly the
    /// guide's own usual land-then-mountains-then-height workflow, so the
    /// imported heightmap's raw byte values - which have no relationship
    /// to this game's legal height bands - never get exported directly).
    /// Rivers and provinces are deliberately left for the normal
    /// Rivers/Provinces stages afterwards, per the chosen "derive
    /// coastline + mountains, finish the rest by hand" workflow - this is
    /// not a one-click full-tile generator the way GenerateRandom is.
    ///
    /// `gray` must already be exactly WidthPx x HeightPx (the App-layer
    /// import dialog is responsible for letting the user crop/resize the
    /// source image to a legal grid size before calling this).
    /// </summary>
    public void ImportHeightmap(GrayMap gray, HeightmapImportOptions options)
    {
        if (gray.Width != WidthPx || gray.Height != HeightPx)
            throw new ArgumentException($"heightmap must already be resized to exactly {WidthPx}x{HeightPx}px, got {gray.Width}x{gray.Height}");

        var land = GrayMap.Bool(WidthPx, HeightPx, false);
        for (int i = 0; i < gray.Data.Length; i++)
            if (EmptyMask.Data[i] < 128 && gray.Data[i] >= options.SeaLevel) land.Data[i] = 255;
        LandMask = land;

        var seaMask = GrayMap.Not(land);
        var widened = RasterOps.WidenAndBlurRidgeLines(gray, seaMask, options.EdgeThreshold, options.LineWidenRadius, options.CorridorRadius, options.BlurSigma);

        MountainMask = new GrayMap(WidthPx, HeightPx);
        int span = Math.Max(255 - options.MountainHeightThreshold, 1);
        for (int i = 0; i < widened.Data.Length; i++)
        {
            if (land.Data[i] < 128) continue;
            int v = widened.Data[i];
            if (v <= options.MountainHeightThreshold) continue;
            MountainMask.Data[i] = (byte)Math.Clamp((v - options.MountainHeightThreshold) * 255 / span, 0, 255);
        }

        GenerateHeight();

        WastelandMask = GrayMap.Bool(WidthPx, HeightPx, false);
        RiverSegments.Clear();
        ImportedRiverRaster = null;
        ProvinceResult = null;
    }

    // -- importing an external reference map image (Kartenbild-Tracing, Runde 8) --

    public sealed class MapTraceOptions
    {
        /// <summary>0-255 grayscale-brightness threshold in the source
        /// image: pixels at or above this become land, below become sea
        /// (or the reverse, if Invert is set). Unlike
        /// HeightmapImportOptions.SeaLevel, this is the ONLY derived
        /// value - a reference map (a real-world map, a screenshot, a
        /// hand-drawn sketch) has no elevation encoding a ridge-line
        /// detector could make sense of the way a purpose-drawn heightmap
        /// sketch does, so tracing only ever derives the coastline, never
        /// mountains.</summary>
        public int Threshold = 128;
        /// <summary>When true, pixels DARKER than Threshold become land
        /// instead of brighter ones - useful for a source image where
        /// land is the dark color (e.g. a black-ink coastline drawn on
        /// white paper, or a real-world map that shades land dark and
        /// water light).</summary>
        public bool Invert = false;
    }

    /// <summary>
    /// Import an external reference map image as the starting point for a
    /// new tile: converts it to grayscale brightness, thresholds it into
    /// LandMask, then runs the normal (mountain-free) height generator
    /// from that flat coastline so the tile is immediately ready for the
    /// normal Mountains/Rivers/Provinces stages - exactly the "derive
    /// coastline, finish the rest by hand" workflow ImportHeightmap uses,
    /// just without any mountain-ridge detection (see MapTraceOptions).
    ///
    /// `gray` must already be exactly WidthPx x HeightPx (the App-layer
    /// import dialog is responsible for resizing the source image to a
    /// legal grid size before calling this, same contract as
    /// ImportHeightmap).
    /// </summary>
    public void ImportMapTrace(GrayMap gray, MapTraceOptions options)
    {
        if (gray.Width != WidthPx || gray.Height != HeightPx)
            throw new ArgumentException($"map image must already be resized to exactly {WidthPx}x{HeightPx}px, got {gray.Width}x{gray.Height}");

        var land = GrayMap.Bool(WidthPx, HeightPx, false);
        for (int i = 0; i < gray.Data.Length; i++)
        {
            // Same EmptyMask consultation as ImportHeightmap: a pixel
            // already marked as "no man's land" stays that way rather
            // than being silently reclaimed as traced land (relevant if
            // this is ever run against something other than a brand-new
            // tile, whose EmptyMask starts all-false anyway).
            if (EmptyMask.Data[i] >= 128) continue;
            bool bright = gray.Data[i] >= options.Threshold;
            if (bright != options.Invert) land.Data[i] = 255;
        }
        LandMask = land;
        MountainMask = new GrayMap(WidthPx, HeightPx);
        GenerateHeight();

        WastelandMask = GrayMap.Bool(WidthPx, HeightPx, false);
        RiverSegments.Clear();
        ImportedRiverRaster = null;
        ProvinceResult = null;
    }

    public int TotalProvinceCount() => ProvinceResult?.Provinces.Count ?? 0;

    public string? ProvinceLimitWarning()
    {
        int n = TotalProvinceCount();
        if (n > Constants.ProvinceHardCap)
            return $"{n} provinces - this is over the known-unsafe limit of about {Constants.ProvinceHardCap}. The tile will likely fail to load.";
        if (n > Constants.ProvinceSoftWarn)
            return $"{n} provinces - getting close to the ~{Constants.ProvinceHardCap} province limit. Consider lowering density.";
        return null;
    }

    /// <summary>Fatal-safety check: no two provinces may share the same
    /// color, since the exported _p.bmp format identifies every province
    /// purely by its RGB color - a collision would make the game treat
    /// two unrelated areas as one province at best, and can crash/fail to
    /// load the tile at worst (Runde 7, vierte Rückmeldung: explicit
    /// request for this safety net). Structurally, every color-assigning
    /// code path already goes through ColorAllocator (which reserves
    /// every already-used color before handing out a new one - see
    /// Colors.cs), so this should never actually trigger; it exists
    /// purely as a last line of defense against a latent bug in some
    /// future/overlooked code path. Returns null when every color is
    /// unique, otherwise a human-readable description of every
    /// collision.</summary>
    public string? FindDuplicateProvinceColors()
    {
        if (ProvinceResult == null) return null;
        var byColor = new Dictionary<Rgb, List<int>>();
        foreach (var info in ProvinceResult.Provinces.Values)
        {
            if (!byColor.TryGetValue(info.Color, out var list)) byColor[info.Color] = list = new List<int>();
            list.Add(info.ProvinceId);
        }
        var collisions = byColor.Where(kv => kv.Value.Count > 1).ToList();
        if (collisions.Count == 0) return null;
        var parts = collisions.Select(kv => $"{kv.Key} used by provinces {string.Join(", ", kv.Value)}");
        return "Duplicate province colors found - exporting would produce a broken/crashing tile: " + string.Join("; ", parts);
    }

    /// <summary>Pixel-graph check of the river layer exactly as it will be exported
    /// (imported raster + all segments). The game crashes with "Circular river" unless
    /// the result is acyclic; see RiverGraphValidator. Never modifies any data.</summary>
    public RiverGraphReport CheckRiverGraph() => RiverGraphValidator.Analyze(RiverRaster(), WidthPx, HeightPx);

    // -- export to game files -------------------------------------------------

    public List<string> Export(string directory)
    {
        if (ProvinceResult == null) throw new InvalidOperationException("Generate or draw provinces before exporting.");
        var colorIssue = FindDuplicateProvinceColors();
        if (colorIssue != null) throw new InvalidOperationException(colorIssue);

        var height = EnsureHeight();
        ClampHeightToLandSafety();
        var riverIdx = RiverRaster();
        var provinces = ProvinceResult;

        var seaColors = provinces.Provinces.Values.Where(p => p.Kind == ProvinceKind.Sea).Select(p => p.Color).ToList();
        var lakeColors = provinces.Provinces.Values.Where(p => p.Kind == ProvinceKind.Lake).Select(p => p.Color).ToList();
        var wasteColors = provinces.Provinces.Values.Where(p => p.Kind == ProvinceKind.Wasteland).Select(p => p.Color).ToList();
        var counts = provinces.Counts();

        // "Moisture" is exported as the game's own add_moisture passthrough
        // field, kept in sync from the Settings slider rather than asking
        // the user to also manage it by hand in the Extra fields list.
        int moistureInt = (int)Math.Round(Math.Clamp(Settings.Moisture, 0, 10000));
        int moistureIdx = Metadata.ExtraFields.FindIndex(kv => kv.Key == "add_moisture");
        if (moistureIdx >= 0) Metadata.ExtraFields[moistureIdx] = ("add_moisture", moistureInt.ToString(CultureInfo.InvariantCulture));
        else Metadata.ExtraFields.Add(("add_moisture", moistureInt.ToString(CultureInfo.InvariantCulture)));

        // Export layout matches the game's own RNW tile folder convention:
        // <tilename>/<tilename>.txt next to a <tilename>/data/ folder
        // holding the three generated bitmaps.
        string tileDir = Path.Combine(directory, Name);
        string dataDir = Path.Combine(tileDir, "data");
        Directory.CreateDirectory(tileDir);
        Directory.CreateDirectory(dataDir);
        string dataBasePath = Path.Combine(dataDir, Name);

        string txtPath = Path.Combine(tileDir, Name + ".txt");
        string hPath = dataBasePath + "_h.bmp";
        string rPath = dataBasePath + "_r.bmp";
        string pPath = dataBasePath + "_p.bmp";

        TextFileIO.WriteTileText(txtPath, new WriteTileOptions
        {
            SizeGrid = (GridW, GridH),
            NumSeaProvinces = counts.GetValueOrDefault(ProvinceKind.Sea),
            NumLandProvinces = counts.GetValueOrDefault(ProvinceKind.Land),
            SeaColors = seaColors,
            LakeColors = lakeColors,
            WastelandColors = wasteColors,
            Metadata = Metadata,
        });
        TileBitmaps.SaveHeightBmp(hPath, height);
        TileBitmaps.SaveRiverBmp(rPath, riverIdx, WidthPx, HeightPx);
        TileBitmaps.SaveProvinceBmp(pPath, provinces.Rgb);

        return new List<string> { txtPath, hPath, rPath, pPath };
    }

    // -- working-project save/load (our own format) ----------------------------

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        SaveTo(fs);
    }

    /// <summary>Same format as Save(path), written to an arbitrary stream -
    /// factored out so the undo/redo manager (UndoManager, App project) can
    /// snapshot a whole project state into an in-memory MemoryStream
    /// without touching disk at all.</summary>
    public void SaveTo(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        void WriteEntry(string name, byte[] data)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var es = entry.Open();
            es.Write(data, 0, data.Length);
        }

        WriteEntry("land_mask.bin", LandMask.Data);
        WriteEntry("mountain_mask.bin", MountainMask.Data);
        WriteEntry("wasteland_mask.bin", WastelandMask.Data);
        WriteEntry("empty_mask.bin", EmptyMask.Data);
        if (Height != null) WriteEntry("height.bin", Height.Data);
        if (ManualProvinceBorderMask != null) WriteEntry("manual_border_mask.bin", ManualProvinceBorderMask.Data);
        if (ImportedRiverRaster != null) WriteEntry("imported_river_raster.bin", ImportedRiverRaster);
        if (ProvinceResult != null) WriteEntry("province_labels.bin", IntArrayToBytes(ProvinceResult.Labels.Data));

        var meta = BuildMetaJson();
        WriteEntry("meta.json", Encoding.UTF8.GetBytes(meta.ToJsonString()));
    }

    public static TileProject Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        return LoadFrom(fs);
    }

    /// <summary>Same format as Load(path), read from an arbitrary stream -
    /// the counterpart to SaveTo(Stream), used by UndoManager to restore an
    /// in-memory snapshot.</summary>
    public static TileProject LoadFrom(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        byte[] ReadEntry(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"project file is missing '{name}'");
            using var es = entry.Open();
            using var ms = new MemoryStream();
            es.CopyTo(ms);
            return ms.ToArray();
        }
        bool HasEntry(string name) => zip.GetEntry(name) != null;

        var meta = JsonNode.Parse(ReadEntry("meta.json"))!.AsObject();

        string name = meta["name"]!.GetValue<string>();
        int gridW = meta["grid_w"]!.GetValue<int>();
        int gridH = meta["grid_h"]!.GetValue<int>();

        var proj = new TileProject(name, gridW, gridH);
        int w = proj.WidthPx, h = proj.HeightPx;

        Array.Copy(ReadEntry("land_mask.bin"), proj.LandMask.Data, w * h);
        Array.Copy(ReadEntry("mountain_mask.bin"), proj.MountainMask.Data, w * h);
        Array.Copy(ReadEntry("wasteland_mask.bin"), proj.WastelandMask.Data, w * h);
        Array.Copy(ReadEntry("empty_mask.bin"), proj.EmptyMask.Data, w * h);

        bool hasHeight = meta["has_height"]?.GetValue<bool>() ?? false;
        if (hasHeight && HasEntry("height.bin"))
        {
            proj.Height = new GrayMap(w, h);
            Array.Copy(ReadEntry("height.bin"), proj.Height.Data, w * h);
        }

        bool hasImportedRiver = meta["has_imported_river_raster"]?.GetValue<bool>() ?? false;
        if (hasImportedRiver && HasEntry("imported_river_raster.bin"))
            proj.ImportedRiverRaster = ReadEntry("imported_river_raster.bin");

        bool hasBorderMask = meta["has_manual_border_mask"]?.GetValue<bool>() ?? false;
        if (hasBorderMask && HasEntry("manual_border_mask.bin"))
        {
            proj.ManualProvinceBorderMask = new GrayMap(w, h);
            Array.Copy(ReadEntry("manual_border_mask.bin"), proj.ManualProvinceBorderMask.Data, w * h);
        }

        proj.RiverSegments = new List<RiverSegment>();
        if (meta["river_segments"] is JsonArray segArr)
        {
            foreach (var segNode in segArr)
            {
                var segObj = segNode!.AsObject();
                var seg = new RiverSegment
                {
                    Size = segObj["size"]!.GetValue<int>(),
                    Junction = ParseJunction(segObj["junction"]!.GetValue<string>()),
                };
                if (segObj["points"] is JsonArray ptsArr)
                {
                    foreach (var ptNode in ptsArr)
                    {
                        var pt = ptNode!.AsArray();
                        seg.Points.Add((pt[0]!.GetValue<int>(), pt[1]!.GetValue<int>()));
                    }
                }
                proj.RiverSegments.Add(seg);
            }
        }

        proj.Metadata = MetadataFromJson(meta["metadata"]!.AsObject());
        proj.Settings = SettingsFromJson(meta["settings"]!.AsObject());

        bool hasProvinces = meta["has_provinces"]?.GetValue<bool>() ?? false;
        if (hasProvinces && HasEntry("province_labels.bin"))
        {
            var labels = new LabelMap(w, h, 0);
            var labelBytes = ReadEntry("province_labels.bin");
            Buffer.BlockCopy(labelBytes, 0, labels.Data, 0, labelBytes.Length);

            var provinces = new Dictionary<int, ProvinceInfo>();
            if (meta["province_info"] is JsonObject provInfoObj)
            {
                foreach (var (pidStr, infoNode) in provInfoObj)
                {
                    int pid = int.Parse(pidStr, CultureInfo.InvariantCulture);
                    var infoObj = infoNode!.AsObject();
                    var colorArr = infoObj["color"]!.AsArray();
                    var seedArr = infoObj["seed"]!.AsArray();
                    provinces[pid] = new ProvinceInfo
                    {
                        ProvinceId = pid,
                        Color = new Rgb((byte)colorArr[0]!.GetValue<int>(), (byte)colorArr[1]!.GetValue<int>(), (byte)colorArr[2]!.GetValue<int>()),
                        Kind = ParseKind(infoObj["kind"]!.GetValue<string>()),
                        PixelCount = infoObj["pixel_count"]!.GetValue<int>(),
                        Seed = (seedArr[0]!.GetValue<int>(), seedArr[1]!.GetValue<int>()),
                    };
                }
            }

            // Color assignment is a deterministic function of sorted
            // province-id order (see ColorAllocator), so re-running it here
            // reproduces the exact colors the province set originally had -
            // nothing needs to be separately restored pixel-by-pixel.
            var rgb = provinces.Count > 0
                ? ProvinceMapGen.ColorizeLabels(labels, provinces, proj.Metadata.EmptyColor)
                : new RgbMap(w, h);
            proj.ProvinceResult = new ProvinceMapResult { Labels = labels, Provinces = provinces, Rgb = rgb };
        }

        return proj;
    }

    // -- loading an existing game tile (to modify it) --------------------------

    public static TileProject LoadExistingTile(string txtPath)
    {
        string name = Path.GetFileNameWithoutExtension(txtPath);
        string directory = Path.GetDirectoryName(txtPath) is { Length: > 0 } d ? d : ".";

        // The game's own tile layout (matches this program's own Export,
        // see the WriteTileOptions call below) is <name>/<name>.txt next to
        // a <name>/data/ subfolder holding the three bitmaps - the .txt's
        // directory itself is NOT where the bitmaps live. Some tiles instead
        // keep everything side by side, or nest the .txt one level deeper
        // than the bitmaps. So: try the "data" subfolder first (the real
        // convention), then next to the .txt, then the parent directory,
        // before giving up.
        string? bmpDir = null;
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, "data"),
                     directory,
                     Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                 })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (File.Exists(Path.Combine(candidate, $"{name}_h.bmp")) &&
                File.Exists(Path.Combine(candidate, $"{name}_r.bmp")) &&
                File.Exists(Path.Combine(candidate, $"{name}_p.bmp")))
            {
                bmpDir = candidate;
                break;
            }
        }
        bmpDir ??= directory; // fall through to the original error below

        string hPath = Path.Combine(bmpDir, $"{name}_h.bmp");
        string rPath = Path.Combine(bmpDir, $"{name}_r.bmp");
        string pPath = Path.Combine(bmpDir, $"{name}_p.bmp");
        foreach (var p in new[] { hPath, rPath, pPath })
            if (!File.Exists(p))
                throw new FileNotFoundException($"Expected {Path.GetFileName(p)} in a 'data' subfolder next to {Path.GetFileName(txtPath)} (or next to it, or one folder above), but it is missing.");

        var parsed = TextFileIO.ParseTileText(txtPath);
        var height = TileBitmaps.LoadHeightBmp(hPath);
        var riverIdx = TileBitmaps.LoadRiverBmp(rPath);
        var provinceRgb = TileBitmaps.LoadProvinceBmp(pPath);

        int pxW = height.Width, pxH = height.Height;

        // Always derive the grid size from the actual height bitmap's
        // pixel dimensions, never from the .txt's declared "size" clause:
        // every mask this method builds below is sized pxW x pxH (the
        // ground truth), so trusting a "size" value that happens to
        // disagree with it would leave WidthPx/HeightPx (what the canvas
        // uses for zoom/fit/crop math) mismatched against the actual
        // arrays - previously a likely source of an out-of-range crash
        // the moment the GUI tried to display anything beyond the initial
        // view. Round to the nearest whole 128px cell in case the bitmap
        // is off by a pixel or two.
        // (If the .txt's declared "size" clause disagrees with the bitmap,
        // the bitmap wins - it's what every pixel operation below actually
        // indexes into.)
        int gridW = Math.Max(1, (int)Math.Round(pxW / (double)Constants.GridUnit));
        int gridH = Math.Max(1, (int)Math.Round(pxH / (double)Constants.GridUnit));

        var proj = new TileProject(name, gridW, gridH);
        proj.Metadata = parsed.Metadata;
        if (parsed.Weight != 0) proj.Metadata.Weight = parsed.Weight;

        var seaSet = new HashSet<Rgb>(parsed.SeaProvinces);
        var lakeSet = new HashSet<Rgb>(parsed.LakeProvinces);
        var wasteSet = new HashSet<Rgb>(parsed.WastelandProvinces);
        var waterSet = new HashSet<Rgb>(seaSet);
        waterSet.UnionWith(lakeSet);

        var (labelArray, colorCounts, colorToId) = ProvinceMapGen.LoadFromExistingBmp(provinceRgb);

        // Classify each unique color's label id up front (cheap - one pass
        // over the distinct colors), then apply that classification to the
        // masks and find each label's first-seen seed pixel in a single
        // O(pixels) pass - avoids the O(pixels * provinceCount) cost a
        // naive "one mask-fill pass per province" port would have, which
        // matters once a tile is anywhere near the ~1000 province cap.
        var kindByPid = new ProvinceKind[colorToId.Count];
        foreach (var (color, _) in colorCounts)
        {
            int pid = colorToId[color];
            ProvinceKind kind;
            if (seaSet.Contains(color)) kind = ProvinceKind.Sea;
            else if (lakeSet.Contains(color)) kind = ProvinceKind.Lake;
            else if (wasteSet.Contains(color)) kind = ProvinceKind.Wasteland;
            else if (color.Equals(parsed.Metadata.EmptyColor) && !waterSet.Contains(color) && !wasteSet.Contains(color)) kind = ProvinceKind.Empty;
            else kind = ProvinceKind.Land;
            kindByPid[pid] = kind;
        }

        var landMask = GrayMap.Bool(pxW, pxH, true);
        var wastelandMask = GrayMap.Bool(pxW, pxH, false);
        var emptyMaskOut = GrayMap.Bool(pxW, pxH, false);
        var seedByLabel = new (int x, int y)?[colorToId.Count];

        for (int y = 0; y < pxH; y++)
        {
            for (int x = 0; x < pxW; x++)
            {
                int pid = labelArray[x, y];
                switch (kindByPid[pid])
                {
                    case ProvinceKind.Sea:
                    case ProvinceKind.Lake:
                        landMask.SetBool(x, y, false);
                        break;
                    case ProvinceKind.Empty:
                        landMask.SetBool(x, y, false);
                        emptyMaskOut.SetBool(x, y, true);
                        break;
                    case ProvinceKind.Wasteland:
                        wastelandMask.SetBool(x, y, true);
                        break;
                }
                if (seedByLabel[pid] == null) seedByLabel[pid] = (x, y);
            }
        }

        var provinces = new Dictionary<int, ProvinceInfo>();
        foreach (var (color, count) in colorCounts)
        {
            int pid = colorToId[color];
            provinces[pid] = new ProvinceInfo
            {
                ProvinceId = pid,
                Color = color,
                Kind = kindByPid[pid],
                PixelCount = count,
                Seed = seedByLabel[pid] ?? (0, 0),
            };
        }

        proj.LandMask = landMask;
        proj.WastelandMask = wastelandMask;
        proj.EmptyMask = emptyMaskOut;
        proj.Height = height;
        proj.ImportedRiverRaster = riverIdx;
        proj.ProvinceResult = new ProvinceMapResult { Labels = labelArray, Provinces = provinces, Rgb = provinceRgb };

        // Rough approximation of a "mountains" paint layer for further
        // editing: anything noticeably above the plain-land baseline.
        proj.MountainMask = new GrayMap(pxW, pxH);
        for (int i = 0; i < proj.MountainMask.Data.Length; i++)
        {
            if (landMask.Data[i] < 128) continue;
            double mtn = Math.Clamp((height.Data[i] - Constants.HeightLandBase - 20) / 60.0, 0.0, 1.0);
            proj.MountainMask.Data[i] = (byte)Math.Clamp(Math.Round(mtn * 255.0), 0, 255);
        }

        return proj;
    }

    // -- JSON (de)serialization helpers -----------------------------------------

    private static byte[] IntArrayToBytes(int[] arr)
    {
        var bytes = new byte[arr.Length * sizeof(int)];
        Buffer.BlockCopy(arr, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static ProvinceKind ParseKind(string s) => s.ToLowerInvariant() switch
    {
        "sea" => ProvinceKind.Sea,
        "lake" => ProvinceKind.Lake,
        "wasteland" => ProvinceKind.Wasteland,
        "empty" => ProvinceKind.Empty,
        _ => ProvinceKind.Land,
    };

    private static RiverJunction ParseJunction(string s) => s.ToLowerInvariant() switch
    {
        "merge" => RiverJunction.Merge,
        "split" => RiverJunction.Split,
        _ => RiverJunction.Source,
    };

    private JsonObject BuildMetaJson()
    {
        var segArr = new JsonArray();
        foreach (var seg in RiverSegments)
        {
            var ptsArr = new JsonArray();
            foreach (var (x, y) in seg.Points) ptsArr.Add(new JsonArray { x, y });
            segArr.Add(new JsonObject
            {
                ["points"] = ptsArr,
                ["size"] = seg.Size,
                ["junction"] = seg.Junction.ToString().ToLowerInvariant(),
            });
        }

        var provInfoObj = new JsonObject();
        if (ProvinceResult != null)
        {
            foreach (var (pid, info) in ProvinceResult.Provinces)
            {
                provInfoObj[pid.ToString(CultureInfo.InvariantCulture)] = new JsonObject
                {
                    ["color"] = new JsonArray { info.Color.R, info.Color.G, info.Color.B },
                    ["kind"] = info.Kind.ToString().ToLowerInvariant(),
                    ["pixel_count"] = info.PixelCount,
                    ["seed"] = new JsonArray { info.Seed.x, info.Seed.y },
                };
            }
        }

        return new JsonObject
        {
            ["name"] = Name,
            ["grid_w"] = GridW,
            ["grid_h"] = GridH,
            ["has_height"] = Height != null,
            ["has_manual_border_mask"] = ManualProvinceBorderMask != null,
            ["has_imported_river_raster"] = ImportedRiverRaster != null,
            ["has_provinces"] = ProvinceResult != null,
            ["river_segments"] = segArr,
            ["province_info"] = provInfoObj,
            ["metadata"] = MetadataToJson(Metadata),
            ["settings"] = SettingsToJson(Settings),
        };
    }

    private static JsonObject MetadataToJson(TileMetadata m)
    {
        var straitsArr = new JsonArray();
        foreach (var s in m.Straits)
        {
            straitsArr.Add(new JsonArray
            {
                new JsonArray { s.From.R, s.From.G, s.From.B },
                new JsonArray { s.To.R, s.To.G, s.To.B },
                new JsonArray { s.Through.R, s.Through.G, s.Through.B },
            });
        }

        var regionsArr = new JsonArray();
        foreach (var c in m.Regions) regionsArr.Add(new JsonArray { c.R, c.G, c.B });

        var modifiersObj = new JsonObject();
        foreach (var (k, colors) in m.Modifiers)
        {
            var arr = new JsonArray();
            foreach (var c in colors) arr.Add(new JsonArray { c.R, c.G, c.B });
            modifiersObj[k] = arr;
        }

        var provinceNamesObj = new JsonObject();
        foreach (var (rgb, provName) in m.ProvinceNames)
            provinceNamesObj[$"{rgb.R},{rgb.G},{rgb.B}"] = provName;

        var extraObj = new JsonObject();
        foreach (var (k, v) in m.ExtraFields) extraObj[k] = v;

        return new JsonObject
        {
            ["do_not_rotate"] = m.DoNotRotate,
            ["do_not_rotate_or_mirror"] = m.DoNotRotateOrMirror,
            ["restrict_to_north_edge"] = m.RestrictToNorthEdge,
            ["restrict_to_south_edge"] = m.RestrictToSouthEdge,
            ["continent"] = m.Continent,
            ["fantasy"] = m.Fantasy,
            ["unique"] = m.Unique.HasValue ? JsonValue.Create(m.Unique.Value) : null,
            ["weight"] = m.Weight,
            ["straits"] = straitsArr,
            ["regions"] = regionsArr,
            ["modifiers"] = modifiersObj,
            ["province_names"] = provinceNamesObj,
            ["empty_color"] = new JsonArray { m.EmptyColor.R, m.EmptyColor.G, m.EmptyColor.B },
            ["extra_fields"] = extraObj,
        };
    }

    private static TileMetadata MetadataFromJson(JsonObject d)
    {
        var m = new TileMetadata
        {
            DoNotRotate = d["do_not_rotate"]!.GetValue<bool>(),
            DoNotRotateOrMirror = d["do_not_rotate_or_mirror"]!.GetValue<bool>(),
            RestrictToNorthEdge = d["restrict_to_north_edge"]!.GetValue<bool>(),
            RestrictToSouthEdge = d["restrict_to_south_edge"]!.GetValue<bool>(),
            Continent = d["continent"]!.GetValue<bool>(),
            Fantasy = d["fantasy"]!.GetValue<bool>(),
            Unique = d["unique"]?.GetValue<int>(),
            Weight = d["weight"]!.GetValue<int>(),
        };

        static Rgb RgbFromArr(JsonNode? n)
        {
            var a = n!.AsArray();
            return new Rgb((byte)a[0]!.GetValue<int>(), (byte)a[1]!.GetValue<int>(), (byte)a[2]!.GetValue<int>());
        }

        m.Straits = new List<Strait>();
        if (d["straits"] is JsonArray straitsArr)
        {
            foreach (var sNode in straitsArr)
            {
                var sArr = sNode!.AsArray();
                m.Straits.Add(new Strait { From = RgbFromArr(sArr[0]), To = RgbFromArr(sArr[1]), Through = RgbFromArr(sArr[2]) });
            }
        }

        m.Regions = new List<Rgb>();
        if (d["regions"] is JsonArray regionsArr)
            foreach (var c in regionsArr) m.Regions.Add(RgbFromArr(c));

        m.Modifiers = new Dictionary<string, List<Rgb>>();
        if (d["modifiers"] is JsonObject modsObj)
        {
            foreach (var (k, v) in modsObj)
            {
                var list = new List<Rgb>();
                if (v is JsonArray arr) foreach (var c in arr) list.Add(RgbFromArr(c));
                m.Modifiers[k] = list;
            }
        }

        m.ProvinceNames = new Dictionary<Rgb, string>();
        if (d["province_names"] is JsonObject pnObj)
        {
            foreach (var (k, v) in pnObj)
            {
                var parts = k.Split(',');
                var rgb = new Rgb(byte.Parse(parts[0], CultureInfo.InvariantCulture), byte.Parse(parts[1], CultureInfo.InvariantCulture), byte.Parse(parts[2], CultureInfo.InvariantCulture));
                m.ProvinceNames[rgb] = v!.GetValue<string>();
            }
        }

        if (d["empty_color"] is JsonArray ecArr) m.EmptyColor = RgbFromArr(ecArr);

        m.ExtraFields = new List<(string, string)>();
        if (d["extra_fields"] is JsonObject exObj)
            foreach (var (k, v) in exObj) m.ExtraFields.Add((k, v!.GetValue<string>()));

        return m;
    }

    private static JsonObject SettingsToJson(GenerationSettings s) => new()
    {
        ["land_rise_distance"] = s.LandRiseDistance,
        ["land_interior_height"] = s.LandInteriorHeight,
        ["mountain_boost"] = s.MountainBoost,
        ["sea_fade_distance"] = s.SeaFadeDistance,
        ["blur_coastlines"] = s.BlurCoastlines,
        ["blur_mountains"] = s.BlurMountains,
        ["land_density_per_cell"] = s.LandDensityPerCell,
        ["wasteland_density_per_cell"] = s.WastelandDensityPerCell,
        ["sea_spacing"] = s.SeaSpacing,
        ["land_obscurity"] = s.LandObscurity,
        ["border_curviness"] = s.BorderCurviness,
        ["land_size_variance"] = s.LandSizeVariance,
        ["random_seed"] = s.RandomSeed.HasValue ? JsonValue.Create(s.RandomSeed.Value) : null,
        ["wasteland_by_height_enabled"] = s.WastelandByHeightEnabled,
        ["wasteland_height_threshold"] = s.WastelandHeightThreshold,
        ["sea_size_gradient"] = s.SeaSizeGradient,
        ["experimental_empty_far_sea"] = s.ExperimentalEmptyFarSea,
        ["sea_coastal_area_multiplier"] = s.SeaCoastalAreaMultiplier,
        ["sea_ocean_area_multiplier"] = s.SeaOceanAreaMultiplier,
        ["moisture"] = s.Moisture,
    };

    private static GenerationSettings SettingsFromJson(JsonObject d) => new()
    {
        LandRiseDistance = d["land_rise_distance"]!.GetValue<double>(),
        LandInteriorHeight = d["land_interior_height"]!.GetValue<double>(),
        MountainBoost = d["mountain_boost"]!.GetValue<double>(),
        SeaFadeDistance = d["sea_fade_distance"]!.GetValue<double>(),
        // Projects saved before the coastline/mountain blur split existed
        // only have a single "blur_sigma" - use it as the coastline
        // baseline and derive a somewhat stronger mountain blur from it,
        // rather than throwing on an old project file.
        BlurCoastlines = d["blur_coastlines"]?.GetValue<double>() ?? d["blur_sigma"]?.GetValue<double>() ?? 2.5,
        BlurMountains = d["blur_mountains"]?.GetValue<double>() ?? Math.Max((d["blur_sigma"]?.GetValue<double>() ?? 2.5) * 1.6, (d["blur_sigma"]?.GetValue<double>() ?? 2.5) + 1.5),
        LandDensityPerCell = d["land_density_per_cell"]!.GetValue<double>(),
        WastelandDensityPerCell = d["wasteland_density_per_cell"]!.GetValue<double>(),
        SeaSpacing = d["sea_spacing"]!.GetValue<double>(),
        // Missing in projects saved before this option existed - default
        // to a neutral obscurity rather than throwing.
        LandObscurity = d["land_obscurity"]?.GetValue<double>() ?? 3.0,
        // Missing in projects saved before this option existed.
        BorderCurviness = d["border_curviness"]?.GetValue<double>() ?? 0.4,
        LandSizeVariance = d["land_size_variance"]?.GetValue<double>() ?? 0.0,
        RandomSeed = d["random_seed"]?.GetValue<int>(),
        WastelandByHeightEnabled = d["wasteland_by_height_enabled"]?.GetValue<bool>() ?? false,
        WastelandHeightThreshold = d["wasteland_height_threshold"]?.GetValue<double>() ?? 130,
        SeaSizeGradient = d["sea_size_gradient"]?.GetValue<bool>() ?? true,
        ExperimentalEmptyFarSea = d["experimental_empty_far_sea"]?.GetValue<bool>() ?? false,
        SeaCoastalAreaMultiplier = d["sea_coastal_area_multiplier"]?.GetValue<double>() ?? 5.0,
        SeaOceanAreaMultiplier = d["sea_ocean_area_multiplier"]?.GetValue<double>() ?? 20.0,
        Moisture = d["moisture"]?.GetValue<double>() ?? 5000,
    };
}
