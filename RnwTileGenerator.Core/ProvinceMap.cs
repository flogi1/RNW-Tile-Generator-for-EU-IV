using System;
using System.Collections.Generic;
using System.Linq;

namespace RnwTileGenerator.Core;

/// <summary>
/// Province map (_p.bmp) construction: turning a land/sea/wasteland/empty
/// mask into a set of uniquely-colored province cells. Direct port of the
/// original Python "rnw/provincemap.py" module, with the cKDTree-based
/// nearest-seed assignment replaced by RasterOps.VoronoiAssign's
/// multi-source BFS (graph distance within the domain mask rather than
/// pure Euclidean distance - a very close approximation for the roughly
/// convex domains a coastline/mountain painting produces, and dependency
/// free).
///
/// Automatic mode places jittered seed points at roughly the spacing the
/// guide recommends (sea provinces ~125px "long", land provinces averaging
/// 6-8 per 128x128 cell, at least 30x30px) and then assigns every pixel to
/// its nearest seed, separately within the land+wasteland domain and the
/// sea+lake domain so the two never bleed into each other. Manual mode
/// instead takes province polygons the user painted directly (as a plain
/// label array) and just needs colors assigned.
/// </summary>
public enum ProvinceKind
{
    Land, Sea, Lake, Wasteland,
    /// <summary>Only produced by LoadExistingTile: a pixel colored exactly
    /// as the tile's declared "empty" color that isn't also a declared
    /// sea/lake/wasteland color. Faithfully mirrors a quirk of the original
    /// Python loader, which still records an empty-colored region as a
    /// province entry (see TileProject.LoadExistingTile) even though it is
    /// excluded from the land mask and flagged in the empty mask.</summary>
    Empty,
}

public sealed class ProvinceInfo
{
    public int ProvinceId;
    public Rgb Color;
    public ProvinceKind Kind;
    public int PixelCount;
    public (int x, int y) Seed;
}

public sealed class ProvinceMapResult
{
    public required LabelMap Labels;   // -1 = empty/unassigned
    public required Dictionary<int, ProvinceInfo> Provinces;
    public required RgbMap Rgb;

    public Dictionary<ProvinceKind, int> Counts()
    {
        var outCounts = new Dictionary<ProvinceKind, int>
        {
            [ProvinceKind.Land] = 0,
            [ProvinceKind.Sea] = 0,
            [ProvinceKind.Lake] = 0,
            [ProvinceKind.Wasteland] = 0,
        };
        foreach (var p in Provinces.Values) outCounts[p.Kind] = outCounts.GetValueOrDefault(p.Kind) + 1;
        return outCounts;
    }
}

public sealed class ProvinceGenOptions
{
    public double LandDensityPerCell = Constants.LandProvincesPerCellDefault;
    public double WastelandDensityPerCell = Constants.WastelandProvincesPerCellDefault;
    public double SeaSpacing = Constants.SeaProvinceTargetSpacingDefault;
    public Rgb EmptyColor = Constants.DefaultEmptyColor;
    public int? Seed;

    /// <summary>1-10. How irregular land province shapes/boundaries come
    /// out: 1 keeps seed placement close to a regular grid and boundaries
    /// close to plain nearest-seed; 10 jitters seeds much more freely and
    /// adds a noisy wobble to the boundary-growing cost field. Independent
    /// of - and combined with - the river/mountain boundary bias below.</summary>
    public double LandObscurity = 3.0;

    /// <summary>Optional: when supplied, plain-land province boundaries
    /// are biased to prefer running along rivers rather than crossing
    /// them (a real border is far likelier to follow a river than cut
    /// through open land next to one).</summary>
    public GrayMap? RiverMask;

    /// <summary>Optional: when supplied, plain-land province boundaries
    /// are biased to prefer running along mountain ridgelines (byte
    /// intensity 0..255, same convention as TileProject.MountainMask)
    /// rather than cutting straight through a mountain range.</summary>
    public GrayMap? MountainMask;

    /// <summary>0-1. Softens the straight, faceted province boundaries
    /// VoronoiAssign's graph-distance BFS otherwise produces into an
    /// organically wavy line, using the same coastline-detail-style noise
    /// idea as RandomTileOptions.CoastlineDetail (see
    /// RasterOps.AddBorderCurviness). 0 leaves the raw boundary untouched;
    /// independent of - and applied after - LandObscurity's own
    /// seed-jitter/cost-wobble irregularity. RasterOps.AddBorderCurviness
    /// itself blends two noise scales (a broad sweep plus a finer wobble),
    /// aimed at the "real historical borders" look (e.g. Holy Roman Empire
    /// state borders) rather than a single-frequency ripple.</summary>
    public double BorderCurviness = 0.4;

    /// <summary>0-1. How much automatically generated land province sizes
    /// vary from one region of the map to another, instead of reading as
    /// a fairly uniform grid: modulates the local seed spacing with a
    /// broad, coherent noise field so some regions end up with visibly
    /// bigger provinces and others with visibly smaller ones. 0 (default)
    /// keeps the original evenly-spaced behavior. See
    /// ProvinceMapGen.JitteredSeedsVariableDensity.</summary>
    public double LandSizeVariance = 0.0;

    /// <summary>Optional: when supplied together with WastelandHeightThreshold,
    /// any land pixel at or above that height is folded into the wasteland
    /// domain regardless of the painted WastelandMask - so wasteland
    /// provinces can be generated straight from "everything above this
    /// altitude", with their shapes naturally following the height contour
    /// (they can only ever be assigned pixels within that contour to begin
    /// with, since VoronoiAssign never crosses outside its domain mask).</summary>
    public GrayMap? Height;
    public double? WastelandHeightThreshold;

    /// <summary>Sea provinces are smaller/denser near any coastline (and
    /// throughout small/mostly-enclosed seas, so fleets keep room to
    /// maneuver), noticeably bigger in "open" water past that, and bigger
    /// again in "deep" water far from everything - three tiers instead of
    /// one uniform spacing everywhere, see GenerateAutomatic. Sea provinces
    /// in general are allowed to read a fair bit bigger than land
    /// provinces, and open/deep-ocean ones bigger again; angular-looking
    /// shapes out there are an accepted trade-off of the larger spacing
    /// (Runde 6 feedback: the previous two-tier version still produced a
    /// handful of oddly-cut, similarly-sized wedge provinces out in open
    /// water rather than a few clearly-bigger ones).</summary>
    public bool SeaSizeGradient = true;
    /// <summary>Distance from land (px) that still counts as "coastal" for
    /// SeaSizeGradient.</summary>
    public double SeaCoastalBandPx = 90;
    /// <summary>Distance from land (px) beyond which water counts as "deep"
    /// rather than merely "open" - the sparsest of the three tiers. Default
    /// is 4x the default SeaCoastalBandPx.</summary>
    public double SeaDeepBandPx = 360;

    /// <summary>How large a coastal sea province should read, expressed as
    /// a multiple of the average LAND province's area (not an absolute
    /// pixel spacing) - directly exposed as a slider (Runde 7 feedback: the
    /// user asked for exactly this framing, "coastal sea zones roughly 5x
    /// an average province, open ocean roughly 20x", rather than more
    /// internal tuning of a fixed heuristic). Converted to a seed spacing
    /// via spacing = averageLandProvinceSide * sqrt(multiplier), since
    /// area scales with the square of spacing.</summary>
    public double SeaCoastalAreaMultiplier = 5.0;
    /// <summary>Same idea as SeaCoastalAreaMultiplier, for the "open ocean"
    /// tier (past-coastal, before-deep).</summary>
    public double SeaOceanAreaMultiplier = 20.0;
    /// <summary>Extra multiplier applied on TOP of the open-ocean spacing
    /// (itself derived from SeaOceanAreaMultiplier) for the "deep" tier -
    /// deliberately bigger again so a large empty ocean reads as a handful
    /// of clearly-bigger provinces instead of many similar-sized wedges.
    /// Not separately exposed as a slider (Runde 7 feedback asked for a
    /// coastal/ocean split specifically, not a third dial) - it simply
    /// scales up whatever the ocean slider is set to.</summary>
    public double SeaDeepSpacingFactor = 1.7;

    /// <summary>Experimental: any generated sea province in the "deep"
    /// tier (see SeaDeepBandPx) is dropped entirely rather than kept as an
    /// ordinary sea province, to save province budget on open water far
    /// from anything - the pixels are simply left unassigned
    /// (rendered/exported as the tile's empty background, which the game
    /// itself then fills with an auto-placed sea zone) instead.</summary>
    public bool ExperimentalEmptyFarSea = false;
}

public static class ProvinceMapGen
{
    /// <summary>True where a water pixel belongs to the open sea (connected
    /// to the tile border), false where it is an enclosed lake. Only
    /// meaningful where waterMask is true.</summary>
    public static GrayMap ClassifyWater(GrayMap waterMask) => RasterOps.BorderConnected(waterMask);

    /// <summary>Jittered-grid seed points, kept only where `mask` is true,
    /// with a minimum-separation rejection pass (via a spatial hash grid,
    /// so it stays fast even with thousands of candidates) so seeds never
    /// end up on top of each other after jitter. Public: also used by
    /// TileProject.GenerateProvincesInRegion for the "regenerate provinces
    /// in this rectangle" tool.</summary>
    public static List<(int x, int y)> JitteredSeeds(GrayMap mask, double spacing, Random rng, double? minSepOverride = null, double jitterFraction = 0.35)
    {
        int w = mask.Width, h = mask.Height;
        if (spacing <= 1) spacing = 1;
        double minSep = minSepOverride ?? spacing * 0.55;

        var candidates = new List<(double x, double y)>();
        for (double gy = spacing / 2; gy < h; gy += spacing)
        {
            for (double gx = spacing / 2; gx < w; gx += spacing)
            {
                double jx = gx + (rng.NextDouble() * 2 - 1) * spacing * jitterFraction;
                double jy = gy + (rng.NextDouble() * 2 - 1) * spacing * jitterFraction;
                int ix = (int)Math.Round(jx), iy = (int)Math.Round(jy);
                if (ix >= 0 && ix < w && iy >= 0 && iy < h && mask.Data[iy * w + ix] >= 128)
                    candidates.Add((ix, iy));
            }
        }
        if (candidates.Count == 0) return new List<(int, int)>();

        double cell = Math.Max(minSep, 1e-6);
        var grid = new Dictionary<(int, int), List<(double x, double y)>>();
        var accepted = new List<(int x, int y)>();
        foreach (var pt in candidates)
        {
            int cx = (int)Math.Floor(pt.x / cell), cy = (int)Math.Floor(pt.y / cell);
            bool tooClose = false;
            for (int oy = -1; oy <= 1 && !tooClose; oy++)
            {
                for (int ox = -1; ox <= 1 && !tooClose; ox++)
                {
                    if (grid.TryGetValue((cx + ox, cy + oy), out var list))
                    {
                        foreach (var p2 in list)
                        {
                            double dx = pt.x - p2.x, dy = pt.y - p2.y;
                            if (dx * dx + dy * dy < minSep * minSep) { tooClose = true; break; }
                        }
                    }
                }
            }
            if (tooClose) continue;
            if (!grid.TryGetValue((cx, cy), out var bucket)) grid[(cx, cy)] = bucket = new List<(double, double)>();
            bucket.Add(pt);
            accepted.Add(((int)pt.x, (int)pt.y));
        }
        return accepted;
    }

    /// <summary>Like JitteredSeeds, but the minimum-separation rejection
    /// radius is modulated by a broad coherent noise field instead of
    /// being one constant value everywhere - regions where the noise
    /// reads high end up with a bigger local minimum separation (fewer,
    /// bigger provinces), regions where it reads low end up smaller
    /// (denser, smaller provinces), instead of the uniformly-sized grid
    /// JitteredSeeds otherwise produces. Candidates are drawn from a
    /// finer base grid than requested (so sparse-noise regions still have
    /// enough nearby candidates to fill in) and visited in random order
    /// (so acceptance doesn't systematically favor one part of the grid
    /// once local separation varies).</summary>
    public static List<(int x, int y)> JitteredSeedsVariableDensity(GrayMap mask, double spacing, Random rng, double sizeVariance, int noiseSeed, double jitterFraction = 0.35)
    {
        if (spacing <= 1) spacing = 1;
        if (sizeVariance <= 0.001) return JitteredSeeds(mask, spacing, rng, jitterFraction: jitterFraction);
        sizeVariance = Math.Clamp(sizeVariance, 0.0, 1.0);

        int w = mask.Width, h = mask.Height;
        double gridStep = spacing * 0.5;
        double baseMinSep = spacing * 0.55;
        var noise = NoiseGen.GenerateField(w, h, Math.Max(spacing * 3.0, 20.0), 2, 0.5, 2.0, noiseSeed);

        var candidates = new List<(double x, double y)>();
        for (double gy = gridStep / 2; gy < h; gy += gridStep)
        {
            for (double gx = gridStep / 2; gx < w; gx += gridStep)
            {
                double jx = gx + (rng.NextDouble() * 2 - 1) * gridStep * jitterFraction;
                double jy = gy + (rng.NextDouble() * 2 - 1) * gridStep * jitterFraction;
                int ix = (int)Math.Round(jx), iy = (int)Math.Round(jy);
                if (ix >= 0 && ix < w && iy >= 0 && iy < h && mask.Data[iy * w + ix] >= 128)
                    candidates.Add((ix, iy));
            }
        }
        if (candidates.Count == 0) return new List<(int, int)>();
        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        double cell = Math.Max(baseMinSep * (1.0 + sizeVariance), 1e-6);
        var grid = new Dictionary<(int, int), List<(double x, double y, double minSep)>>();
        var accepted = new List<(int x, int y)>();
        foreach (var pt in candidates)
        {
            int nx = Math.Clamp((int)pt.x, 0, w - 1), ny = Math.Clamp((int)pt.y, 0, h - 1);
            double n = noise[ny * w + nx]; // roughly [-1, 1]
            double localMinSep = baseMinSep * Math.Clamp(1.0 + sizeVariance * n, 0.35, 1.0 + sizeVariance * 1.3);

            int cx = (int)Math.Floor(pt.x / cell), cy = (int)Math.Floor(pt.y / cell);
            bool tooClose = false;
            for (int oy = -1; oy <= 1 && !tooClose; oy++)
            {
                for (int ox = -1; ox <= 1 && !tooClose; ox++)
                {
                    if (grid.TryGetValue((cx + ox, cy + oy), out var list))
                    {
                        foreach (var p2 in list)
                        {
                            double dx = pt.x - p2.x, dy = pt.y - p2.y;
                            double sep = Math.Max(localMinSep, p2.minSep);
                            if (dx * dx + dy * dy < sep * sep) { tooClose = true; break; }
                        }
                    }
                }
            }
            if (tooClose) continue;
            if (!grid.TryGetValue((cx, cy), out var bucket)) grid[(cx, cy)] = bucket = new();
            bucket.Add((pt.x, pt.y, localMinSep));
            accepted.Add(((int)pt.x, (int)pt.y));
        }
        return accepted;
    }

    /// <summary>Guarantees every disconnected 4-connected component of
    /// `domainMask` that has at least one pixel contains at least one of
    /// `seeds` - if a component's grid-based jittered candidates all
    /// happened to miss it (easy for a small island, a thin isthmus, or any
    /// domain much smaller than the configured spacing), a single
    /// representative-pixel seed is added for it so AddDomain's
    /// VoronoiAssign still reaches every pixel of it. Without this, a
    /// missed component stayed completely unlabeled and rendered as the
    /// tile's plain black "empty" background - previously visible as small,
    /// seemingly random black patches with no obvious criteria (Runde 7
    /// feedback).</summary>
    private static List<(int x, int y)> EnsureSeedPerComponent(GrayMap domainMask, List<(int x, int y)> seeds)
    {
        var (compLabels, compCount) = RasterOps.LabelRegions(domainMask, GrayMap.Bool(domainMask.Width, domainMask.Height, false));
        if (compCount == 0) return seeds;
        int w = domainMask.Width, h = domainMask.Height;

        var covered = new bool[compCount + 1];
        foreach (var (sx, sy) in seeds)
        {
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
            int c = compLabels[sy * w + sx];
            if (c > 0) covered[c] = true;
        }

        var extra = new (int x, int y)?[compCount + 1];
        int stillMissing = compCount;
        for (int i = 0; i < compLabels.Length && stillMissing > 0; i++)
        {
            int c = compLabels[i];
            if (c <= 0 || covered[c] || extra[c].HasValue) continue;
            extra[c] = (i % w, i / w);
            stillMissing--;
        }

        var result = seeds;
        foreach (var e in extra)
            if (e.HasValue) { if (ReferenceEquals(result, seeds)) result = new List<(int x, int y)>(seeds); result.Add(e.Value); }
        return result;
    }

    /// <summary>Adds one Voronoi-assigned domain's worth of provinces
    /// (fresh ids starting at `nextId`, which is advanced past the last
    /// one used) into the shared `labels`/`provinces` - the innermost
    /// building block both GenerateAutomatic and the two standalone
    /// per-domain regenerators below are built from.</summary>
    private static void AddDomain(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, ref int nextId,
        GrayMap domainMask, List<(int x, int y)> seeds, ProvinceKind kind, byte[]? cellCost = null)
    {
        if (seeds.Count == 0) return;
        var assign = cellCost != null
            ? RasterOps.WeightedVoronoiAssign(domainMask, seeds, cellCost)
            : RasterOps.VoronoiAssign(domainMask, seeds);
        var counts = new int[seeds.Count];
        for (int i = 0; i < assign.Length; i++) if (assign[i] >= 0) counts[assign[i]]++;

        var idMap = new int[seeds.Count];
        for (int localIdx = 0; localIdx < seeds.Count; localIdx++)
        {
            if (counts[localIdx] == 0) { idMap[localIdx] = -1; continue; }
            int pid = nextId++;
            idMap[localIdx] = pid;
            provinces[pid] = new ProvinceInfo
            {
                ProvinceId = pid,
                Color = default,
                Kind = kind,
                PixelCount = counts[localIdx],
                Seed = seeds[localIdx],
            };
        }
        for (int i = 0; i < assign.Length; i++)
        {
            if (assign[i] < 0) continue;
            int pid = idMap[assign[i]];
            if (pid >= 0) labels.Data[i] = pid;
        }
    }

    /// <summary>Builds and adds the land+wasteland side of automatic
    /// province generation into `labels`/`provinces` - factored out of
    /// GenerateAutomatic (Runde 7, dritte Rückmeldung) so
    /// TileProject.RegenerateLandProvinces can also call it standalone, on
    /// top of an existing sea/lake layout that stays completely untouched
    /// ("Küste behalten, nur die Landprovinz-Einteilung neu würfeln").
    /// Outputs the plainLand/wastelandDomain masks it computed, since the
    /// caller needs them again for the border-curviness and gap-fill
    /// passes that must run after every domain has final labels.</summary>
    internal static void GenerateLandDomain(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, ref int nextId,
        GrayMap landMask, GrayMap wastelandMask, GrayMap emptyMask, ProvinceGenOptions options, Random rng,
        out GrayMap plainLand, out GrayMap wastelandDomain)
    {
        double cellArea = (double)Constants.GridUnit * Constants.GridUnit;
        var land = GrayMap.And(landMask, GrayMap.Not(emptyMask));
        var wasteland = GrayMap.And(wastelandMask, land);

        // Wasteland-by-height: fold every land pixel above the configured
        // altitude into the wasteland domain too, on top of whatever was
        // hand-painted - shapes generated in there are automatically
        // confined to (and so follow) that height contour, since
        // VoronoiAssign can only ever reach pixels inside its domain mask.
        if (options.WastelandHeightThreshold.HasValue && options.Height != null)
        {
            var heightWaste = GrayMap.Bool(land.Width, land.Height, false);
            byte thresholdByte = (byte)Math.Clamp(options.WastelandHeightThreshold.Value, 0, 255);
            for (int i = 0; i < land.Data.Length; i++)
                if (land.Data[i] >= 128 && options.Height.Data[i] >= thresholdByte) heightWaste.Data[i] = 255;
            wasteland = GrayMap.Or(wasteland, heightWaste);
        }

        plainLand = GrayMap.And(land, GrayMap.Not(wasteland));
        wastelandDomain = wasteland;

        double landSpacing = Math.Max(Math.Sqrt(cellArea / Math.Max(options.LandDensityPerCell, 0.1)), Constants.LandProvinceMinSide);
        double wasteSpacing = Math.Max(Math.Sqrt(cellArea / Math.Max(options.WastelandDensityPerCell, 0.1)), Constants.LandProvinceMinSide);

        // Obscurity only really means anything for LAND province shapes
        // (that's what the guide's "realistic" advice is about) - sea/lake
        // spacing stay on the plain default jitter. 1 = close to a
        // regular grid, 10 = seeds scattered much more freely.
        double obscurityT = Math.Clamp((options.LandObscurity - 1) / 9.0, 0.0, 1.0);
        double landJitter = 0.12 + obscurityT * 0.33; // 0.12 .. 0.45

        var landSeeds = options.LandSizeVariance > 0.001
            ? JitteredSeedsVariableDensity(plainLand, landSpacing, rng, options.LandSizeVariance, (options.Seed ?? Environment.TickCount) + 424242, jitterFraction: landJitter)
            : JitteredSeeds(plainLand, landSpacing, rng, jitterFraction: landJitter);
        var wasteSeeds = JitteredSeeds(wastelandDomain, wasteSpacing, rng, jitterFraction: landJitter);

        // Guarantee every disconnected piece of the domain (a small
        // island, a thin isthmus, a narrow strait channel, ...) got at
        // least one seed, even if the jittered grid missed it entirely -
        // see EnsureSeedPerComponent's own doc comment for why this matters
        // (Runde 7: unexplained black patches with no clear cause).
        landSeeds = EnsureSeedPerComponent(plainLand, landSeeds);
        wasteSeeds = EnsureSeedPerComponent(wastelandDomain, wasteSeeds);

        // Plain-land boundary cost field: rivers and mountain ridges cost
        // extra to step onto (so the wavefronts from two neighboring seeds
        // hesitate to cross them, and settle their shared border on top of
        // the feature instead of cutting through it), plus - scaled by
        // obscurity - a noisy wobble so higher obscurity produces rougher,
        // less clean-Voronoi boundaries even away from any river/mountain.
        // This is a first-pass heuristic, not a geography simulation: it
        // consistently nudges borders toward rivers/ridgelines, but real
        // tiles' borders follow many more cues than this alone captures.
        byte[]? landCost = null;
        if (options.RiverMask != null || options.MountainMask != null || obscurityT > 0)
        {
            int n = landMask.Width * landMask.Height;
            landCost = new byte[n];
            float[]? noise = obscurityT > 0
                ? NoiseGen.GenerateField(landMask.Width, landMask.Height, Math.Max(landSpacing * 0.5, 6), 3, 0.5, 2.0, options.Seed ?? Environment.TickCount)
                : null;
            double noiseSwing = obscurityT * 4.0; // up to +-2 extra cost at full obscurity
            for (int i = 0; i < n; i++)
            {
                double c = 1.0;
                if (options.RiverMask != null && options.RiverMask.Data[i] >= 128) c += 7.0;
                if (options.MountainMask != null) c += options.MountainMask.Data[i] / 255.0 * 5.0;
                if (noise != null) c += (noise[i] + 1.0) * 0.5 * noiseSwing;
                landCost[i] = (byte)Math.Clamp(Math.Round(c), 1, 250);
            }
        }

        AddDomain(labels, provinces, ref nextId, plainLand, landSeeds, ProvinceKind.Land, landCost);
        AddDomain(labels, provinces, ref nextId, wastelandDomain, wasteSeeds, ProvinceKind.Wasteland);
    }

    /// <summary>Builds and adds the sea+lake side of automatic province
    /// generation into `labels`/`provinces` - factored out of
    /// GenerateAutomatic (Runde 7, dritte Rückmeldung) so
    /// TileProject.RegenerateSeaProvinces can also call it standalone, on
    /// top of an existing land/wasteland layout that stays completely
    /// untouched. Outputs the seaMask/lakeMask/distToLand it computed,
    /// since the caller needs seaMask/lakeMask again for the gap-fill pass
    /// and distToLand for the experimental far-sea pruning.</summary>
    internal static void GenerateWaterDomain(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, ref int nextId,
        GrayMap landMask, GrayMap emptyMask, ProvinceGenOptions options, Random rng,
        out GrayMap seaMask, out GrayMap lakeMask, out float[]? distToLand)
    {
        var land = GrayMap.And(landMask, GrayMap.Not(emptyMask));
        var waterMask = GrayMap.And(GrayMap.Not(land), GrayMap.Not(emptyMask));
        var isSea = ClassifyWater(waterMask);
        seaMask = GrayMap.And(waterMask, isSea);
        lakeMask = GrayMap.And(waterMask, GrayMap.Not(isSea));
        distToLand = null;

        double cellArea = (double)Constants.GridUnit * Constants.GridUnit;
        double landSpacing = Math.Max(Math.Sqrt(cellArea / Math.Max(options.LandDensityPerCell, 0.1)), Constants.LandProvinceMinSide);

        // Sea province sizing: dense/small near any coastline (real sea
        // lanes are busiest close to shore) and near/throughout small,
        // mostly-enclosed seas (so fleets there still have room to
        // maneuver); noticeably bigger in "open" water past that; bigger
        // again in "deep" water far from everything - three tiers, so a
        // large empty ocean reads as a handful of clearly-bigger provinces
        // rather than many similarly-sized wedge shapes. Accepting that
        // open/deep-ocean provinces read a bit more angular at that
        // spacing, the same trade-off the tile guide makes for its own
        // "big open sea" provinces. Lakes (already split out via
        // ClassifyWater above) keep their own always-dense spacing
        // regardless of this option.
        List<(int x, int y)> seaSeeds;
        if (options.SeaSizeGradient && seaMask.AnyTrue())
        {
            distToLand = RasterOps.DistanceFrom(land);
            var (compLabels, compCount) = RasterOps.LabelRegions(seaMask, GrayMap.Bool(seaMask.Width, seaMask.Height, false));
            var maxDistPerComp = new float[compCount + 1];
            for (int i = 0; i < compLabels.Length; i++)
            {
                int c = compLabels[i];
                if (c <= 0) continue;
                if (distToLand[i] > maxDistPerComp[c]) maxDistPerComp[c] = distToLand[i];
            }
            var coastalMask = GrayMap.Bool(seaMask.Width, seaMask.Height, false);
            var openMask = GrayMap.Bool(seaMask.Width, seaMask.Height, false);
            var deepMask = GrayMap.Bool(seaMask.Width, seaMask.Height, false);
            for (int i = 0; i < compLabels.Length; i++)
            {
                int c = compLabels[i];
                if (c <= 0) continue;
                bool smallEnclosedComponent = maxDistPerComp[c] < options.SeaCoastalBandPx * 1.6;
                if (smallEnclosedComponent || distToLand[i] < options.SeaCoastalBandPx) coastalMask.Data[i] = 255;
                else if (distToLand[i] < options.SeaDeepBandPx) openMask.Data[i] = 255;
                else deepMask.Data[i] = 255;
            }
            // Spacing is derived from average LAND province size (landSpacing
            // is exactly that - see above) rather than the sea density
            // slider's own SeaSpacing, per the Runde 7 request to frame sea
            // zone size directly as "N times an average province" - area
            // scales with spacing squared, hence the sqrt.
            double coastalSpacing = Math.Max(landSpacing * Math.Sqrt(Math.Max(options.SeaCoastalAreaMultiplier, 0.2)), 18);
            double openSpacing = Math.Max(landSpacing * Math.Sqrt(Math.Max(options.SeaOceanAreaMultiplier, 0.5)), coastalSpacing);
            double deepSpacing = openSpacing * Math.Max(options.SeaDeepSpacingFactor, 1.0);
            seaSeeds = JitteredSeeds(coastalMask, coastalSpacing, rng);
            seaSeeds.AddRange(JitteredSeeds(openMask, openSpacing, rng));
            seaSeeds.AddRange(JitteredSeeds(deepMask, deepSpacing, rng));
        }
        else
        {
            seaSeeds = JitteredSeeds(seaMask, options.SeaSpacing, rng);
        }
        var lakeSeeds = JitteredSeeds(lakeMask, Math.Max(options.SeaSpacing * 0.6, 20), rng);

        seaSeeds = EnsureSeedPerComponent(seaMask, seaSeeds);
        lakeSeeds = EnsureSeedPerComponent(lakeMask, lakeSeeds);

        AddDomain(labels, provinces, ref nextId, seaMask, seaSeeds, ProvinceKind.Sea);
        AddDomain(labels, provinces, ref nextId, lakeMask, lakeSeeds, ProvinceKind.Lake);

        // Consolidate every lake on the tile into a single province (Runde
        // 7, vierte Rückmeldung: "inländische Seeprovinzen haben praktisch
        // keinen klassischen Nutzen, wenn sie nicht schon praktisch ein
        // Meer sind") - an individual lake's own shape still comes from
        // AddDomain's normal per-component seeding above (so the merge
        // logic doesn't need any special-casing for disconnected lake
        // bodies, which JitteredSeeds/EnsureSeedPerComponent already
        // handle correctly), this just collapses the resulting handful of
        // separate Lake-kind provinces down to one right after they're
        // created, before any pixel-count refresh or gap-filling downstream.
        // Scoped to Lake only, not Sea - open sea provinces remain fully
        // meaningful and are left untouched.
        MergeAllOfKind(labels, provinces, ProvinceKind.Lake);
    }

    /// <summary>Refreshes every province's PixelCount from the current
    /// `labels` grid - border curviness and gap-filling can each grow a
    /// province beyond what AddDomain originally counted.</summary>
    private static void RefreshPixelCounts(LabelMap labels, Dictionary<int, ProvinceInfo> provinces)
    {
        var freshCounts = new Dictionary<int, int>();
        for (int i = 0; i < labels.Data.Length; i++)
        {
            int pid = labels.Data[i];
            if (pid < 0) continue;
            freshCounts[pid] = freshCounts.GetValueOrDefault(pid) + 1;
        }
        foreach (var info in provinces.Values) info.PixelCount = freshCounts.GetValueOrDefault(info.ProvinceId);
    }

    /// <summary>Drops any sea province in the "deep" tier (see
    /// ProvinceGenOptions.SeaDeepBandPx) when ExperimentalEmptyFarSea is
    /// on - saves province budget on open water far from anything, at the
    /// cost of that water simply reading as the tile's empty background
    /// instead of a sailable-but-pointless province. Factored out of
    /// GenerateAutomatic so TileProject.RegenerateSeaProvinces applies the
    /// exact same rule.</summary>
    internal static void PruneExperimentalFarSea(LabelMap labels, Dictionary<int, ProvinceInfo> provinces,
        GrayMap landMask, GrayMap emptyMask, ProvinceGenOptions options, float[]? distToLand)
    {
        if (!options.ExperimentalEmptyFarSea) return;
        bool anySea = false;
        foreach (var info in provinces.Values) if (info.Kind == ProvinceKind.Sea) { anySea = true; break; }
        if (!anySea) return;

        var land = GrayMap.And(landMask, GrayMap.Not(emptyMask));
        distToLand ??= RasterOps.DistanceFrom(land);
        double farThreshold = Math.Max(options.SeaDeepBandPx, options.SeaSpacing * 1.8);
        var minDistPerPid = new Dictionary<int, float>();
        for (int i = 0; i < labels.Data.Length; i++)
        {
            int pid = labels.Data[i];
            if (pid < 0 || !provinces.TryGetValue(pid, out var info) || info.Kind != ProvinceKind.Sea) continue;
            float d = distToLand[i];
            if (!minDistPerPid.TryGetValue(pid, out var cur) || d < cur) minDistPerPid[pid] = d;
        }
        foreach (var (pid, minDist) in minDistPerPid)
        {
            if (minDist < farThreshold) continue;
            for (int i = 0; i < labels.Data.Length; i++)
                if (labels.Data[i] == pid) labels.Data[i] = -1;
            provinces.Remove(pid);
        }
    }

    public static ProvinceMapResult GenerateAutomatic(
        GrayMap landMask,
        GrayMap wastelandMask,
        GrayMap emptyMask,
        ProvinceGenOptions? options = null)
    {
        options ??= new ProvinceGenOptions();
        var rng = options.Seed.HasValue ? new Random(options.Seed.Value) : new Random();

        var labels = new LabelMap(landMask.Width, landMask.Height);
        var provinces = new Dictionary<int, ProvinceInfo>();
        int nextId = 0;

        GenerateLandDomain(labels, provinces, ref nextId, landMask, wastelandMask, emptyMask, options, rng, out var plainLand, out var wastelandDomain);
        GenerateWaterDomain(labels, provinces, ref nextId, landMask, emptyMask, options, rng, out var seaMask, out var lakeMask, out var distToLand);

        // Border curviness is a separate post-process pass (rather than
        // folded into the cost-field wobble above) so it can run after
        // EVERY domain already has final labels - flipping a boundary
        // pixel needs to see its actual neighboring province id, which
        // doesn't exist yet while AddDomain is still running.
        if (options.BorderCurviness > 0.001)
        {
            int curvinessSeed = options.Seed ?? Environment.TickCount;
            RasterOps.AddBorderCurviness(labels, plainLand, options.BorderCurviness, curvinessSeed);
            RasterOps.AddBorderCurviness(labels, wastelandDomain, options.BorderCurviness, curvinessSeed + 999);
        }

        // Belt-and-suspenders on top of EnsureSeedPerComponent above: make
        // absolutely certain no domain pixel is left unlabeled (which would
        // otherwise render as the tile's plain black "empty" background -
        // Runde 7 feedback about unexplained black patches). Runs BEFORE
        // the experimental deep-sea pruning below, which deliberately
        // re-introduces -1 labels of its own that must NOT get refilled.
        RasterOps.FillUnlabeledGapsWithinDomain(labels, plainLand);
        RasterOps.FillUnlabeledGapsWithinDomain(labels, wastelandDomain);
        RasterOps.FillUnlabeledGapsWithinDomain(labels, seaMask);
        RasterOps.FillUnlabeledGapsWithinDomain(labels, lakeMask);

        RefreshPixelCounts(labels, provinces);

        PruneExperimentalFarSea(labels, provinces, landMask, emptyMask, options, distToLand);

        // Final correctness floor: no province (of any kind, from any
        // source above) is allowed to survive under
        // Constants.MinProvincePixels - see MergeTinyProvinces (Runde 7,
        // dritte Rückmeldung: user found single-pixel lakes slipping
        // through).
        MergeTinyProvinces(labels, provinces, Constants.MinProvincePixels);

        var rgb = Colorize(labels, provinces, options.EmptyColor);
        return new ProvinceMapResult { Labels = labels, Provinces = provinces, Rgb = rgb };
    }

    private static RgbMap Colorize(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, Rgb emptyColor)
    {
        int w = labels.Width, h = labels.Height;
        var rgb = new RgbMap(w, h);
        for (int i = 0; i < rgb.Data.Length; i++) rgb.Data[i] = emptyColor;

        var allocator = new ColorAllocator(new[] { emptyColor });
        foreach (var pid in provinces.Keys.OrderBy(k => k))
        {
            var kind = provinces[pid].Kind;
            var palette = kind is ProvinceKind.Sea or ProvinceKind.Lake ? ColorPalette.Sea : ColorPalette.Land;
            provinces[pid].Color = allocator.Next(palette);
        }

        if (provinces.Count > 0)
        {
            int maxId = provinces.Keys.Max();
            var lut = new Rgb[maxId + 1];
            foreach (var kv in provinces) lut[kv.Key] = kv.Value.Color;
            for (int i = 0; i < labels.Data.Length; i++)
            {
                int lbl = labels.Data[i];
                if (lbl >= 0) rgb.Data[i] = lut[lbl];
            }
        }
        return rgb;
    }

    /// <summary>Re-run color assignment in place (e.g. after province
    /// edits), keeping the label array and classifications untouched.</summary>
    public static void Recolor(ProvinceMapResult result, Rgb? emptyColor = null) =>
        result.Rgb = Colorize(result.Labels, result.Provinces, emptyColor ?? Constants.DefaultEmptyColor);

    /// <summary>Merges every province with fewer than `minPixels` pixels
    /// into a bordering neighbor, so no unplayable sliver-province - e.g.
    /// a single-pixel lake - survives (Runde 7, dritte Rückmeldung). Not a
    /// configurable generation style, just a correctness floor: callers
    /// pass Constants.MinProvincePixels. Runs in passes (a merge can
    /// occasionally still leave the result under the threshold) until
    /// stable or a small iteration cap is hit; only mutates `labels`/
    /// `provinces` - callers are responsible for repainting/recoloring
    /// afterward (Colorize, RepaintRgb, or ColorizeNewOnly + RepaintRgb).
    ///
    /// Prefers the same-kind neighbor with the most shared border. When
    /// `restrictToSameKind` is false (the default - safe for a full/
    /// regional regeneration, which is free to touch anything) it falls
    /// back to the best neighbor of ANY kind if no same-kind one touches
    /// it, so e.g. a 1px lake fully enclosed by land still gets absorbed
    /// instead of surviving untouched. When `restrictToSameKind` is true
    /// (used by TileProject.RegenerateLandProvinces/RegenerateSeaProvinces,
    /// which must never touch the other, untouched domain) a tiny province
    /// with no same-kind neighbor at all is left as-is instead of risking
    /// a cross-domain merge that would corrupt the domain being kept.</summary>
    public static void MergeTinyProvinces(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, int minPixels, bool restrictToSameKind = false)
    {
        if (minPixels <= 1 || provinces.Count == 0) return;
        int w = labels.Width, h = labels.Height;

        for (int pass = 0; pass < 8; pass++)
        {
            var tinySet = new HashSet<int>();
            foreach (var kv in provinces) if (kv.Value.PixelCount < minPixels) tinySet.Add(kv.Key);
            if (tinySet.Count == 0) return;

            var neighborVotes = new Dictionary<int, Dictionary<int, int>>();
            var pixelsByTiny = new Dictionary<int, List<int>>();
            foreach (int id in tinySet) { neighborVotes[id] = new Dictionary<int, int>(); pixelsByTiny[id] = new List<int>(); }

            // Only non-tiny neighbors get a vote: that guarantees whatever
            // id ends up chosen as a merge target below is stable for the
            // rest of this pass (never itself simultaneously merged away),
            // so relabeling pixels straight to it is always safe.
            void Vote(int lbl, int nlbl)
            {
                if (nlbl < 0 || nlbl == lbl || tinySet.Contains(nlbl)) return;
                var votes = neighborVotes[lbl];
                votes[nlbl] = votes.GetValueOrDefault(nlbl) + 1;
            }

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = row + x;
                    int lbl = labels.Data[idx];
                    if (lbl < 0 || !tinySet.Contains(lbl)) continue;
                    pixelsByTiny[lbl].Add(idx);
                    if (x > 0) Vote(lbl, labels.Data[idx - 1]);
                    if (x < w - 1) Vote(lbl, labels.Data[idx + 1]);
                    if (y > 0) Vote(lbl, labels.Data[idx - w]);
                    if (y < h - 1) Vote(lbl, labels.Data[idx + w]);
                }
            }

            bool anyMerge = false;
            foreach (int id in tinySet)
            {
                var votes = neighborVotes[id];
                if (votes.Count == 0 || !provinces.TryGetValue(id, out var info)) continue; // no non-tiny neighbor yet - try again next pass
                var kind = info.Kind;
                int best = -1, bestVotes = -1, bestSameKind = -1, bestSameKindVotes = -1;
                foreach (var (nid, v) in votes)
                {
                    if (!provinces.TryGetValue(nid, out var ninfo)) continue;
                    if (v > bestVotes) { best = nid; bestVotes = v; }
                    if (ninfo.Kind == kind && v > bestSameKindVotes) { bestSameKind = nid; bestSameKindVotes = v; }
                }
                int target = bestSameKind >= 0 ? bestSameKind : (restrictToSameKind ? -1 : best);
                if (target < 0) continue;

                foreach (int idx in pixelsByTiny[id]) labels.Data[idx] = target;
                provinces[target].PixelCount += info.PixelCount;
                provinces.Remove(id);
                anyMerge = true;
            }
            if (!anyMerge) return;
        }
    }

    /// <summary>Merges every existing province of `kind` into a single
    /// survivor province (the one with the most pixels keeps its id/color/
    /// seed) - used for ProvinceKind.Lake (Runde 7, vierte Rückmeldung):
    /// individual inland lake provinces have essentially no gameplay value
    /// unless they're already practically a sea, so rather than spending
    /// province budget on many small separate lake bodies, every lake on
    /// the tile becomes one single province. Safe to call with zero or one
    /// province of the kind (no-op either way). Only mutates `labels`/
    /// `provinces` - callers already run RefreshPixelCounts and/or
    /// Colorize/RepaintRgb afterward, same as MergeTinyProvinces.</summary>
    public static void MergeAllOfKind(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, ProvinceKind kind)
    {
        var ids = provinces.Where(kv => kv.Value.Kind == kind).Select(kv => kv.Key).ToList();
        if (ids.Count <= 1) return;

        int keep = ids.OrderByDescending(id => provinces[id].PixelCount).First();
        var toMerge = new HashSet<int>(ids);
        toMerge.Remove(keep);

        for (int i = 0; i < labels.Data.Length; i++)
            if (toMerge.Contains(labels.Data[i])) labels.Data[i] = keep;

        int totalPixels = 0;
        foreach (int id in ids) totalPixels += provinces[id].PixelCount;
        provinces[keep].PixelCount = totalPixels;
        foreach (int id in toMerge) provinces.Remove(id);
    }

    /// <summary>Assigns fresh colors only to the province ids in `newIds` -
    /// every other province's existing Color is reserved and left
    /// completely untouched. Used by TileProject.RegenerateLandProvinces/
    /// RegenerateSeaProvinces so re-rolling one domain never reshuffles
    /// the colors of provinces outside it (unlike Recolor/Colorize, which
    /// reassign every color).</summary>
    public static void ColorizeNewOnly(Dictionary<int, ProvinceInfo> provinces, HashSet<int> newIds, Rgb emptyColor)
    {
        var allocator = new ColorAllocator(new[] { emptyColor });
        foreach (var kv in provinces) if (!newIds.Contains(kv.Key)) allocator.Reserve(kv.Value.Color);
        foreach (int id in newIds.OrderBy(x => x))
        {
            if (!provinces.TryGetValue(id, out var info)) continue;
            var palette = info.Kind is ProvinceKind.Sea or ProvinceKind.Lake ? ColorPalette.Sea : ColorPalette.Land;
            info.Color = allocator.Next(palette);
        }
    }

    /// <summary>Repaints `rgb` purely from the current `labels`/
    /// `provinces` (each pixel gets its owning province's Color, or
    /// `emptyColor` if unassigned) - used after an operation that only
    /// mutates ids/labels/colors in place, without going through the full
    /// Colorize (which would reassign every color).</summary>
    public static void RepaintRgb(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, RgbMap rgb, Rgb emptyColor)
    {
        for (int i = 0; i < labels.Data.Length; i++)
        {
            int pid = labels.Data[i];
            rgb.Data[i] = pid >= 0 && provinces.TryGetValue(pid, out var info) ? info.Color : emptyColor;
        }
    }

    /// <summary>Public entry point for paint-by-label-array coloring, used
    /// by TileProject.Load to recolor a restored label array. Color
    /// assignment is a deterministic function of sorted province id order
    /// (see ColorAllocator), so this reproduces the exact same colors a
    /// province set originally got - nothing needs to be separately
    /// persisted and restored.</summary>
    public static RgbMap ColorizeLabels(LabelMap labels, Dictionary<int, ProvinceInfo> provinces, Rgb emptyColor) =>
        Colorize(labels, provinces, emptyColor);

    /// <summary>Build a ProvinceMapResult from a manually-painted label
    /// array (e.g. from the manual province-drawing tool), where `kindOf`
    /// classifies each label id as land/sea/lake/wasteland.</summary>
    public static ProvinceMapResult FromLabelArray(LabelMap labels, IReadOnlyDictionary<int, ProvinceKind> kindOf, Rgb? emptyColor = null)
    {
        var color = emptyColor ?? Constants.DefaultEmptyColor;
        var counts = new Dictionary<int, int>();
        var seeds = new Dictionary<int, (int, int)>();
        for (int y = 0; y < labels.Height; y++)
        {
            for (int x = 0; x < labels.Width; x++)
            {
                int pid = labels[x, y];
                if (pid < 0) continue;
                if (!counts.ContainsKey(pid)) { counts[pid] = 0; seeds[pid] = (x, y); }
                counts[pid]++;
            }
        }
        var provinces = new Dictionary<int, ProvinceInfo>();
        foreach (var kv in counts)
        {
            provinces[kv.Key] = new ProvinceInfo
            {
                ProvinceId = kv.Key,
                Color = default,
                Kind = kindOf.TryGetValue(kv.Key, out var k) ? k : ProvinceKind.Land,
                PixelCount = kv.Value,
                Seed = seeds[kv.Key],
            };
        }
        // Same correctness floor as automatic generation (Runde 7, dritte
        // Rückmeldung) - a hand-drawn border can just as easily leave a
        // tiny sliver behind (e.g. a stray unclosed pen stroke fragment).
        MergeTinyProvinces(labels, provinces, Constants.MinProvincePixels);
        var rgb = Colorize(labels, provinces, color);
        return new ProvinceMapResult { Labels = labels, Provinces = provinces, Rgb = rgb };
    }

    /// <summary>For the "load an existing tile" feature: turn a loaded
    /// _p.bmp back into a label array (ints) plus a color-to-pixel-count
    /// table and the underlying color-to-id map, so the caller can
    /// cross-reference colors against the parsed text file to classify
    /// each id without re-scanning the whole image.</summary>
    public static (LabelMap labels, Dictionary<Rgb, int> colorCounts, Dictionary<Rgb, int> colorToId) LoadFromExistingBmp(RgbMap rgbMap)
    {
        int w = rgbMap.Width, h = rgbMap.Height;
        var labels = new LabelMap(w, h, 0);
        var colorToId = new Dictionary<Rgb, int>();
        var colorCounts = new Dictionary<Rgb, int>();
        int nextId = 0;
        for (int i = 0; i < rgbMap.Data.Length; i++)
        {
            var c = rgbMap.Data[i];
            if (!colorToId.TryGetValue(c, out int id))
            {
                id = nextId++;
                colorToId[c] = id;
                colorCounts[c] = 0;
            }
            labels.Data[i] = id;
            colorCounts[c] = colorCounts[c] + 1;
        }
        return (labels, colorCounts, colorToId);
    }
}
