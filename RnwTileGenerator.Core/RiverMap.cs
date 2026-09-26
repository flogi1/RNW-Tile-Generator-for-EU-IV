using System;
using System.Collections.Generic;
using System.Linq;

namespace RnwTileGenerator.Core;

/// <summary>
/// River map (_r.bmp) construction. Direct port of the original Python
/// "rnw/rivermap.py" module.
///
/// A whole river system is modeled as a set of RiverSegment polylines:
///
/// - exactly one segment per system has Junction == Source (the green
///   start),
/// - a segment that begins where it flows *into* an already-existing river
///   has Junction == Merge (a red pixel is stamped at its first point,
///   which should be the same pixel as a point on the river it is
///   joining), and
/// - a segment that begins where a distributary branches *off* an existing
///   river has Junction == Split (a yellow pixel, same idea).
///
/// Everything in between is drawn as a 1-pixel wide line in one of nine
/// blue shades according to the segment's Size (1 = widest/main river, 9 =
/// thinnest). The GUI is responsible for letting the user click an
/// existing river pixel to start a merge/split segment so the junction
/// pixel lines up; this module only rasterizes whatever segments it is
/// given.
/// </summary>
public enum RiverJunction { Source, Merge, Split,
    /// <summary>Runde 21: a manually-dabbed pixel patch (StageRivers'
    /// "pixel" tool, for filling in a gap) - rendered as a plain river
    /// body pixel like any other, with no green/red/yellow junction
    /// marker stamped over its first point the way Source/Merge/Split
    /// get, since a patch isn't actually a new source/tributary/branch.</summary>
    Patch }

public sealed class RiverSegment
{
    public List<(int x, int y)> Points { get; set; } = new();
    public int Size { get; set; } = 3; // 1..9, see Constants.RiverMinSize/RiverMaxSize
    public RiverJunction Junction { get; set; } = RiverJunction.Source;

    public byte BluePaletteIndex()
    {
        int s = Math.Max(Constants.RiverMinSize, Math.Min(Constants.RiverMaxSize, Size));
        return (byte)(Constants.RiverPalBlueStart + (s - 1));
    }
}

public static class RiverMapGen
{
    public static byte JunctionPaletteIndex(RiverJunction junction) => junction switch
    {
        RiverJunction.Merge => Constants.RiverPalMerge,
        RiverJunction.Split => Constants.RiverPalSplit,
        _ => Constants.RiverPalSource,
    };

    private static List<(int x, int y)> Bresenham((int x, int y) p0, (int x, int y) p1)
    {
        int x0 = p0.x, y0 = p0.y, x1 = p1.x, y1 = p1.y;
        var points = new List<(int, int)>();
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            points.Add((x, y));
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
        return points;
    }

    /// <summary>Full connected pixel path for a segment (waypoints joined
    /// by straight 1px-thick lines).</summary>
    public static List<(int x, int y)> RasterizeSegment(RiverSegment seg)
    {
        if (seg.Points.Count < 2) return new List<(int, int)>(seg.Points);
        return RasterizePolyline(seg.Points);
    }

    /// <summary>Connected 4-neighbour pixel path through the given points (straight
    /// Bresenham lines between consecutive points, diagonal steps bridged, exact
    /// revisits collapsed). A single point returns that point.</summary>
    public static List<(int x, int y)> RasterizePolyline(IReadOnlyList<(int x, int y)> pts)
    {
        if (pts.Count == 0) return new List<(int, int)>();
        if (pts.Count == 1) return new List<(int, int)> { pts[0] };
        var full = new List<(int, int)>();
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var line = Bresenham(pts[i], pts[i + 1]);
            if (full.Count > 0 && full[^1] == line[0]) line.RemoveAt(0);
            full.AddRange(line);
        }
        return RemoveSelfLoops(MakeFourConnected(full));
    }

    /// <summary>The game reads a river's pixels back to reconstruct its
    /// flow and crashes if that walk ever revisits the exact same pixel
    /// ("Circular river in random map tile at: x, y"). A hand-drawn segment
    /// can loop back on itself this way. (A generalized version of this
    /// that also cut on mere 4-adjacency to any earlier, non-consecutive
    /// point was tried and rejected (see CLAUDE.md, Runde 23): a
    /// perfectly ordinary winding river bend routinely brings two
    /// non-consecutive points within 1 pixel of each other, so that
    /// version also gutted normal, non-degenerate rivers down to almost
    /// nothing. Exact-repeat is the safe, narrow check.) Fix: walk the
    /// final pixel path and, the moment a pixel repeats, drop everything
    /// back to (and including) that repeat - collapsing the loop to its
    /// first visit. The pixel right after the repeat was already
    /// 4-connected to it, so it stays 4-connected to the earlier visit
    /// once the loop is cut.</summary>
    private static List<(int x, int y)> RemoveSelfLoops(List<(int x, int y)> path)
    {
        var result = new List<(int, int)>();
        var indexOf = new Dictionary<(int, int), int>();
        foreach (var pt in path)
        {
            if (indexOf.TryGetValue(pt, out int firstIdx))
            {
                for (int i = result.Count - 1; i > firstIdx; i--)
                {
                    indexOf.Remove(result[i]);
                    result.RemoveAt(i);
                }
            }
            else
            {
                indexOf[pt] = result.Count;
                result.Add(pt);
            }
        }
        return result;
    }

    /// <summary>Bresenham naturally steps diagonally wherever a line isn't
    /// exactly horizontal/vertical/45 degrees, which is only 8-connected -
    /// two pixels touching purely at a corner. The game reads a river's
    /// pixels back with 4-connectivity to reconstruct its flow, so a
    /// diagonal-only step reads as a genuine break: "keine durchgängige
    /// Linie" (Runde 20 user report), same underlying class of bug as the
    /// province border-curviness bleeding fixed in Runde 18. Fix: walk the
    /// rasterized path and, wherever consecutive pixels are a diagonal
    /// neighbor (differ in both x and y), insert one bridging pixel that
    /// shares an edge with both - turning the corner-cut into an
    /// edge-connected "L" - so every step of the final path is 4-connected.</summary>
    private static List<(int x, int y)> MakeFourConnected(List<(int x, int y)> path)
    {
        if (path.Count < 2) return path;
        var result = new List<(int, int)> { path[0] };
        for (int i = 1; i < path.Count; i++)
        {
            var (px, py) = result[^1];
            var (x, y) = path[i];
            if (px != x && py != y) result.Add((x, py)); // bridge pixel, shares an edge with both neighbors
            result.Add((x, y));
        }
        return result;
    }

    /// <summary>Returns a byte (row-major, top-down) array of palette
    /// indices, ready for BmpIO.SaveIndexed8.</summary>
    public static byte[] BuildRiverIndexArray(GrayMap landMask, IReadOnlyList<RiverSegment> segments)
    {
        int w = landMask.Width, h = landMask.Height;
        var idx = new byte[w * h];
        for (int i = 0; i < idx.Length; i++)
            idx[i] = landMask.Data[i] >= 128 ? Constants.RiverPalLandBg : Constants.RiverPalSeaBg;

        // Draw river bodies first, junction markers last, so markers
        // always win over an overlapping river body pixel.
        foreach (var seg in segments)
        {
            byte pal = seg.BluePaletteIndex();
            foreach (var (x, y) in RasterizeSegment(seg))
            {
                if (x >= 0 && x < w && y >= 0 && y < h) idx[y * w + x] = pal;
            }
        }

        foreach (var seg in segments)
        {
            if (seg.Junction == RiverJunction.Patch) continue; // plain body pixel, no marker
            if (seg.Points.Count == 0) continue;
            var (x, y) = seg.Points[0];
            if (x >= 0 && x < w && y >= 0 && y < h) idx[y * w + x] = JunctionPaletteIndex(seg.Junction);
        }

        return idx;
    }

    /// <summary>End points of segments that land on water - candidates for
    /// the height-map "dip below the mouth" treatment.</summary>
    public static List<(int x, int y)> RiverMouths(GrayMap landMask, IReadOnlyList<RiverSegment> segments)
    {
        int w = landMask.Width, h = landMask.Height;
        var mouths = new List<(int, int)>();
        foreach (var seg in segments)
        {
            if (seg.Points.Count == 0) continue;
            var (x, y) = seg.Points[^1];
            if (x >= 0 && x < w && y >= 0 && y < h && landMask.Data[y * w + x] < 128)
                mouths.Add((x, y));
        }
        return mouths;
    }

    /// <summary>
    /// Generates a river network on a coarse grid of `cellSize` px cells (4-10) - see
    /// CoarseRiverGen and docs/superpowers/specs/2026-09-18-coarse-grid-rivers-design.md.
    /// The result never contains a cycle in the pixel graph (the game crashes on those).
    /// `count` river systems (ranked by watershed size) are kept, each one Source trunk
    /// plus Merge segments for its tributaries. `tributaryFrequency` (0-1): channel-
    /// initiation threshold relative to each system's own watershed (0 = trunk only).
    /// `distributaryFrequency` (0-1): chance of a river-mouth arm (Split segment).
    /// `minLengthFraction` (0-1): discard systems whose trunk is shorter than that
    /// fraction of the tile's scale (w + h). `existingRiverRaster` (optional, palette index &lt; 254 =
    /// river pixel): pixels of a river layer that stays on the map; new rivers keep clear of it.
    /// </summary>
    public static List<RiverSegment> AutoGenerateRivers(GrayMap landMask, GrayMap height, int count, Random rng,
        double tributaryFrequency = 0.0, double distributaryFrequency = 0.0, double minLengthFraction = 0.0,
        int cellSize = CoarseRiverGen.DefaultCellSize, byte[]? existingRiverRaster = null)
        => CoarseRiverGen.Generate(landMask, height, count, rng, tributaryFrequency, distributaryFrequency, minLengthFraction, cellSize, existingRiverRaster);

    /// <summary>Maps a branch's accumulated drainage area (as a 0-1
    /// fraction of the biggest system's total on this tile) to a river Size
    /// (1=widest, 9=thinnest) - the bigger the watershed behind a segment,
    /// the wider it reads, Strahler-style. Deterministic (no jitter): once a
    /// real physical quantity drives the choice, added randomness would
    /// only obscure it.</summary>
    internal static int SizeFromAccumFraction(double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        int span = Constants.RiverMaxSize - Constants.RiverMinSize;
        return Constants.RiverMinSize + (int)Math.Round((1.0 - fraction) * span);
    }

    public sealed class ParsedRiverMap
    {
        public List<(int x, int y)> Sources { get; } = new();
        public List<(int x, int y)> Merges { get; } = new();
        public List<(int x, int y)> Splits { get; } = new();
        /// <summary>Keyed by river size 1..9.</summary>
        public Dictionary<int, List<(int x, int y)>> BlueBySize { get; } = new();
        public GrayMap LandMask { get; }

        public ParsedRiverMap(int width, int height) => LandMask = new GrayMap(width, height);
    }

    /// <summary>
    /// Best-effort read-back of an existing river bmp's index array into a
    /// summary usable for display/editing: land/sea background plus raw
    /// colored pixel coordinates grouped by role. Reconstructing the exact
    /// original RiverSegment graph is not possible in general (adjacent
    /// segments merge visually into a single set of colored pixels), so
    /// loaded rivers are exposed as raster overlays the user can paint
    /// over, rather than editable polylines.
    /// </summary>
    public static ParsedRiverMap ParseRiverIndexArray(byte[] idx, int width, int height)
    {
        var result = new ParsedRiverMap(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte v = idx[y * width + x];
                if (v == Constants.RiverPalSource) result.Sources.Add((x, y));
                else if (v == Constants.RiverPalMerge) result.Merges.Add((x, y));
                else if (v == Constants.RiverPalSplit) result.Splits.Add((x, y));
                else if (v >= Constants.RiverPalBlueStart && v < Constants.RiverPalBlueStart + Constants.RiverColorsBlue.Length)
                {
                    int size = v - Constants.RiverPalBlueStart + 1;
                    if (!result.BlueBySize.TryGetValue(size, out var list))
                        result.BlueBySize[size] = list = new List<(int, int)>();
                    list.Add((x, y));
                }

                if (v == Constants.RiverPalLandBg) result.LandMask.Data[y * width + x] = 255;
            }
        }
        return result;
    }
}
