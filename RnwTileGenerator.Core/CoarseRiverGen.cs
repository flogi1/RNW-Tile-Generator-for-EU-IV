using System;
using System.Collections.Generic;
using System.Linq;

namespace RnwTileGenerator.Core;

/// <summary>
/// River network generation on a coarse grid (see docs/superpowers/specs/2026-09-18-
/// coarse-grid-rivers-design.md). Flow accumulation and heavy-path extraction run on
/// k x k pixel cells; each cell path is smoothed (Chaikin) and rasterized, and every
/// segment is drawn upstream through a <see cref="RiverEmitter"/>, which makes cycles in
/// the pixel graph impossible: the game crashes ("Circular river") on any river pixel
/// graph that is not a forest.
/// </summary>
public static class CoarseRiverGen
{
    public const int DefaultCellSize = 6;
    public const int MinCellSize = 4;
    public const int MaxCellSize = 10;

    private const int ChaikinIterations = 2;
    private const int JunctionCandidates = 14;
    private const int MinSegmentFactor = 3;      // a tributary shorter than 3 * k px is dropped
    private const int EarlyAcceptExtra = 20;     // stop searching junction candidates at 3 * k + 20 px

    private static readonly (int dx, int dy)[] Nei8 =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    private sealed class CellSegment
    {
        /// <summary>Trunk: head ... last land cell, then the sea cell it drains into.
        /// Merge: the confluence cell (belongs to the heavier segment) first, then upstream to the head.</summary>
        public List<int> Cells = new();
        /// <summary>Drainage area (in cells) at the segment's downstream end; drives the river Size.</summary>
        public int Accum;
        public bool Trunk;
    }

    private sealed class DrawnSystem
    {
        public List<RiverSegment> Segments = new();
        /// <summary>Emitted trunk pixels in mouth -> source order.</summary>
        public List<(int x, int y)> TrunkPixels = new();
    }

    public static List<RiverSegment> Generate(GrayMap landMask, GrayMap height, int count, Random rng,
        double tributaryFrequency, double distributaryFrequency, double minLengthFraction, int cellSize,
        byte[]? existingRiverRaster = null)
    {
        var result = new List<RiverSegment>();
        if (count <= 0) return result;
        int k = Math.Clamp(cellSize, MinCellSize, MaxCellSize);
        tributaryFrequency = Math.Clamp(tributaryFrequency, 0.0, 1.0);
        distributaryFrequency = Math.Clamp(distributaryFrequency, 0.0, 1.0);
        minLengthFraction = Math.Clamp(minLengthFraction, 0.0, 1.0);
        int w = landMask.Width, h = landMask.Height;

        var grid = CoarseGrid.Build(landMask, height, k);
        var (next, popOrder) = FlowNetworkGen.FillDepressions(grid.Land, grid.Height, grid.Cw, grid.Ch);
        var accum = FlowNetworkGen.Accumulate(grid.Land, next, popOrder);

        var mouths = new List<(int cell, int accum)>();
        for (int i = 0; i < grid.Land.Length; i++)
        {
            if (!grid.Land[i]) continue;
            int dst = next[i];
            if (dst >= 0 && !grid.Land[dst]) mouths.Add((i, accum[i]));
        }
        mouths.Sort((a, b) => b.accum.CompareTo(a.accum));
        if (mouths.Count == 0) return result;

        var emitter = new RiverEmitter(w, h);
        if (existingRiverRaster != null) emitter.MarkExisting(existingRiverRaster);
        double minLengthPx = minLengthFraction * (w + h);
        int maxAccum = mouths[0].accum;
        int kept = 0;

        foreach (var (mouthCell, totalAccum) in mouths)
        {
            if (kept >= count) break;
            var members = CollectMembers(mouthCell, next, grid);
            double threshold = totalAccum * (0.15 - 0.14 * tributaryFrequency);
            var river = new HashSet<int>(members.Where(c => accum[c] >= threshold));
            var cellSegs = ExtractSegments(river, next, accum, grid);
            if (cellSegs.Count == 0) continue;

            var drawn = DrawSystem(cellSegs, grid, landMask, emitter, k, minLengthPx, maxAccum);
            if (drawn == null) continue;
            kept++;
            result.AddRange(drawn.Segments);
            if (distributaryFrequency > 0.001)
                AddDistributaries(drawn, cellSegs[0], river, grid, next, landMask, emitter, rng, distributaryFrequency, k, result);
        }
        return result;
    }

    // -- coarse network ------------------------------------------------------

    /// <summary>Every land cell that drains into <paramref name="mouth"/> (asks each
    /// neighbour "do you flow directly into me?").</summary>
    private static List<int> CollectMembers(int mouth, int[] next, CoarseGrid g)
    {
        var members = new List<int> { mouth };
        var visited = new HashSet<int> { mouth };
        var q = new Queue<int>();
        q.Enqueue(mouth);
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            int cx = c % g.Cw, cy = c / g.Cw;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || nx >= g.Cw || ny < 0 || ny >= g.Ch) continue;
                int n = ny * g.Cw + nx;
                if (visited.Contains(n) || !g.Land[n] || next[n] != c) continue;
                visited.Add(n);
                members.Add(n);
                q.Enqueue(n);
            }
        }
        return members;
    }

    /// <summary>Heavy-path decomposition (as in Runde 22): the heaviest branch at every
    /// confluence continues as the trunk, every other branch becomes a Merge segment that
    /// starts on the confluence cell. The trunk is always element 0.</summary>
    private static List<CellSegment> ExtractSegments(HashSet<int> river, int[] next, int[] accum, CoarseGrid g)
    {
        var segs = new List<CellSegment>();
        if (river.Count == 0) return segs;
        var upstream = new Dictionary<int, int>();
        var heaviest = new Dictionary<int, int>();
        var heaviestAccum = new Dictionary<int, int>();
        foreach (int p in river)
        {
            int px = p % g.Cw, py = p / g.Cw;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= g.Cw || ny < 0 || ny >= g.Ch) continue;
                int n = ny * g.Cw + nx;
                if (!river.Contains(n) || next[n] != p) continue;
                upstream[p] = upstream.GetValueOrDefault(p) + 1;
                if (!heaviestAccum.TryGetValue(p, out int cur) || accum[n] > cur)
                {
                    heaviestAccum[p] = accum[n];
                    heaviest[p] = n;
                }
            }
        }

        foreach (int head in river.Where(p => upstream.GetValueOrDefault(p) == 0).ToList())
        {
            var seg = new CellSegment();
            seg.Cells.Add(head);
            int cur = head;
            while (true)
            {
                int nxt = next[cur];
                bool nxtIsRiver = nxt >= 0 && river.Contains(nxt);
                if (!nxtIsRiver)
                {
                    seg.Cells.Add(nxt);           // the sea cell
                    seg.Trunk = true;
                    break;
                }
                seg.Cells.Add(nxt);
                if (upstream.GetValueOrDefault(nxt) >= 2 && heaviest[nxt] != cur) break; // loses at this confluence
                cur = nxt;
            }
            if (seg.Trunk)
            {
                seg.Accum = accum[seg.Cells[^2]];       // drainage area at the mouth (last land cell)
                segs.Insert(0, seg);
            }
            else
            {
                seg.Cells.Reverse();
                seg.Accum = accum[seg.Cells[1]];        // drainage area just before the confluence
                segs.Add(seg);
            }
        }
        return segs;
    }

    // -- drawing -------------------------------------------------------------

    private static DrawnSystem? DrawSystem(List<CellSegment> segs, CoarseGrid grid, GrayMap landMask,
        RiverEmitter emitter, int k, double minLengthPx, int maxAccum)
    {
        int minTrunk = (int)Math.Ceiling(Math.Max(MinSegmentFactor * (double)k, minLengthPx));
        int minTributary = MinSegmentFactor * k;

        var trunk = segs[0];
        var trunkPath = TrunkPixelPath(trunk, grid, landMask);      // mouth -> source
        int n = emitter.ValidPrefixLength(trunkPath, false);
        if (n < minTrunk) return null;
        var trunkPixels = trunkPath.GetRange(0, n);
        emitter.Commit(trunkPixels, false);

        var system = new DrawnSystem { TrunkPixels = trunkPixels };
        var sourceOrder = new List<(int x, int y)>(trunkPixels);
        sourceOrder.Reverse();
        system.Segments.Add(new RiverSegment
        {
            Points = sourceOrder,
            Size = RiverMapGen.SizeFromAccumFraction((double)trunk.Accum / maxAccum),
            Junction = RiverJunction.Source,
        });

        // which segment "owns" (passes through) each cell; a Merge segment's first cell is
        // its confluence and belongs to the heavier segment, so it is skipped for Merge.
        var owner = new Dictionary<int, int>();
        for (int s = 0; s < segs.Count; s++)
            for (int i = segs[s].Trunk ? 0 : 1; i < segs[s].Cells.Count; i++) owner[segs[s].Cells[i]] = s;

        var pixels = new List<(int x, int y)>?[segs.Count];
        var done = new bool[segs.Count];
        pixels[0] = trunkPixels;
        done[0] = true;
        bool progress = true;
        while (progress)
        {
            progress = false;
            for (int s = 1; s < segs.Count; s++)
            {
                if (done[s]) continue;
                if (!owner.TryGetValue(segs[s].Cells[0], out int parent))
                {
                    done[s] = true; pixels[s] = new(); progress = true; continue;
                }
                if (!done[parent]) continue;     // parent must be drawn first
                done[s] = true;
                progress = true;
                pixels[s] = new();
                var host = pixels[parent]!;
                if (host.Count == 0) continue;

                var poly = CellPolyline(segs[s].Cells, grid, null);
                var path = BestAttachment(poly, host, emitter, k, false);
                if (path.Count < minTributary) continue;
                emitter.Commit(path, true);
                pixels[s] = path;
                system.Segments.Add(new RiverSegment
                {
                    Points = path,
                    Size = RiverMapGen.SizeFromAccumFraction((double)segs[s].Accum / maxAccum),
                    Junction = RiverJunction.Merge,
                });
            }
        }
        return system;
    }

    /// <summary>A distributary is an ordinary Split segment: it leaves the trunk near the
    /// mouth through the lowest neighbouring land cell that is not part of the river,
    /// follows the coarse drainage tree to the sea, and is drawn through the same
    /// emitter. If its whole path cannot be drawn without breaking the rule, the arm is
    /// dropped.</summary>
    private static void AddDistributaries(DrawnSystem system, CellSegment trunk, HashSet<int> riverCells,
        CoarseGrid grid, int[] next, GrayMap landMask, RiverEmitter emitter, Random rng,
        double frequency, int k, List<RiverSegment> result)
    {
        int landCount = trunk.Cells.Count - 1;       // the last trunk cell is the sea cell
        if (landCount < 4) return;
        int window = Math.Max(3, landCount / 4);
        int stride = Math.Max(1, window / 4);
        for (int back = 1; back <= window && back < landCount; back += stride)
        {
            if (rng.NextDouble() >= frequency) continue;
            int c = trunk.Cells[landCount - 1 - back];
            int cx = c % grid.Cw, cy = c / grid.Cw;
            int bestN = -1;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || nx >= grid.Cw || ny < 0 || ny >= grid.Ch) continue;
                int n = ny * grid.Cw + nx;
                if (!grid.Land[n] || riverCells.Contains(n)) continue;
                if (grid.Height[n] > grid.Height[c]) continue;
                if (bestN < 0 || grid.Height[n] < grid.Height[bestN]) bestN = n;
            }
            if (bestN < 0) continue;

            var chain = new List<int> { c, bestN };
            int cur = bestN;
            bool ok = true;
            while (true)
            {
                int nxt = next[cur];
                if (nxt < 0) { ok = false; break; }
                chain.Add(nxt);
                if (!grid.Land[nxt]) break;                 // reached a sea cell
                if (riverCells.Contains(nxt)) { ok = false; break; }   // merged back into the river
                cur = nxt;
            }
            if (!ok) continue;

            var landPart = chain.GetRange(0, chain.Count - 1);
            var target = FindSeaPixel(chain[^1], grid.CenterPixel(landPart[^1]), grid, landMask);
            var poly = CellPolyline(landPart, grid, target);
            var path = BestAttachment(poly, system.TrunkPixels, emitter, k, true);
            if (path.Count == 0) continue;
            emitter.Commit(path, true);
            result.Add(new RiverSegment { Points = path, Size = 4 + rng.Next(4), Junction = RiverJunction.Split });
        }
    }

    /// <summary>Trunk pixel path in mouth -> source order: smoothed land-cell polyline
    /// plus a straight run from the last land cell to the nearest real sea pixel.</summary>
    private static List<(int x, int y)> TrunkPixelPath(CellSegment trunk, CoarseGrid grid, GrayMap landMask)
    {
        var landCells = trunk.Cells.GetRange(0, trunk.Cells.Count - 1);
        int seaCell = trunk.Cells[^1];
        var target = FindSeaPixel(seaCell, grid.CenterPixel(landCells[^1]), grid, landMask);
        var path = RiverMapGen.RasterizePolyline(CellPolyline(landCells, grid, target));
        path.Reverse();
        return path;
    }

    /// <summary>Cell centres -> Chaikin -> optional final pixel (the sea target) -> rounded.</summary>
    private static List<(int x, int y)> CellPolyline(IReadOnlyList<int> landCells, CoarseGrid grid, (int x, int y)? seaTarget)
    {
        var centers = landCells.Select(c =>
        {
            var (px, py) = grid.CenterPixel(c);
            return (x: (double)px, y: (double)py);
        }).ToList();
        var smooth = RiverGeometry.Chaikin(centers, ChaikinIterations);
        if (seaTarget.HasValue) smooth.Add((seaTarget.Value.x, seaTarget.Value.y));
        return RiverGeometry.RoundToPixels(smooth);
    }

    /// <summary>The sea pixel inside <paramref name="seaCell"/> closest to <paramref name="from"/>.
    /// A non-land cell always contains at least one non-land pixel; the cell centre is the fallback.</summary>
    private static (int x, int y) FindSeaPixel(int seaCell, (int x, int y) from, CoarseGrid g, GrayMap landMask)
    {
        int x0 = (seaCell % g.Cw) * g.K, y0 = (seaCell / g.Cw) * g.K;
        var best = g.CenterPixel(seaCell);
        long bestD = long.MaxValue;
        for (int y = y0; y < y0 + g.K; y++)
        {
            for (int x = x0; x < x0 + g.K; x++)
            {
                if (landMask.Data[y * landMask.Width + x] >= 128) continue;
                long d = (long)(x - from.x) * (x - from.x) + (long)(y - from.y) * (y - from.y);
                if (d < bestD) { bestD = d; best = (x, y); }
            }
        }
        return best;
    }

    /// <summary>Tries the nearest host pixels as the junction and returns the drawn path
    /// (junction pixel first) for the best one. With <paramref name="requireFull"/> only a
    /// candidate whose whole path is valid counts; otherwise the longest valid prefix wins.
    /// Returns an empty list when no candidate qualifies.</summary>
    private static List<(int x, int y)> BestAttachment(IReadOnlyList<(int x, int y)> poly,
        IReadOnlyList<(int x, int y)> host, RiverEmitter emitter, int k, bool requireFull)
    {
        var start = poly[0];
        int maxDist = 3 * k;
        var candidates = host
            .Select(p => (p, d: Math.Abs(p.x - start.x) + Math.Abs(p.y - start.y)))
            .Where(t => t.d <= maxDist)
            .OrderBy(t => t.d)
            .Take(JunctionCandidates)
            .Select(t => t.p);
        var best = new List<(int x, int y)>();
        foreach (var cand in candidates)
        {
            var pts = new List<(int x, int y)>(poly.Count) { cand };
            for (int i = 1; i < poly.Count; i++) pts.Add(poly[i]);
            var path = RiverMapGen.RasterizePolyline(pts);
            int n = emitter.ValidPrefixLength(path, true);
            if (requireFull)
            {
                if (n == path.Count) return path;
                continue;
            }
            if (n > best.Count) best = path.GetRange(0, n);
            if (best.Count >= MinSegmentFactor * k + EarlyAcceptExtra) break;
        }
        return best;
    }
}
