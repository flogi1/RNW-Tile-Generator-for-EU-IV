using System;
using System.Collections.Generic;
using System.Linq;

namespace RnwTileGenerator.Core;

public static class RasterOps
{
    private static readonly (int dx, int dy)[] Nei4 = { (1, 0), (-1, 0), (0, 1), (0, -1) };
    private static readonly (int dx, int dy)[] Nei8 =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    // -- brush painting ------------------------------------------------

    /// <summary>Hard-replace every pixel under a filled circle with `value`.
    /// A radius that rounds to 0 (i.e. &lt; 0.5) paints exactly the single
    /// pixel under the cursor - the "1px pen" tools rely on this rather
    /// than clamping every brush to a minimum radius of 1.</summary>
    public static void PaintValue(GrayMap map, double x, double y, double radius, byte value)
    {
        int r = Math.Max(0, (int)Math.Round(radius));
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(map.Width - 1, cx + r);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(map.Height - 1, cy + r);
        long r2 = (long)r * r;
        for (int py = y0; py <= y1; py++)
        {
            long dy = py - cy;
            for (int px = x0; px <= x1; px++)
            {
                long dx = px - cx;
                if (dx * dx + dy * dy <= r2) map[px, py] = value;
            }
        }
    }

    public static void PaintBoolBrush(GrayMap mask, double x, double y, double radius, bool value) =>
        PaintValue(mask, x, y, radius, value ? (byte)255 : (byte)0);

    /// <summary>Repaints a circular brush area of an existing province map
    /// with an already-existing province's exact id/color (Runde 7: the
    /// eyedropper + "paint with picked color" tool on the manual Provinces
    /// stage, for quick edits without going through the whole
    /// draw-borders-then-rebuild workflow). Deliberately restricted to
    /// pixels whose CURRENT province has the same ProvinceKind as the
    /// target - letting a stroke cross Land/Sea/Lake/Wasteland here would
    /// desync LandMask/WastelandMask/etc. from the province raster, which
    /// is exactly the kind of inconsistency the heightmap-safety fix
    /// elsewhere this round (TileProject.ClampHeightToLandSafety) exists to
    /// guard against.</summary>
    public static void PaintProvinceColor(ProvinceMapResult result, double x, double y, double radius, int targetPid)
    {
        if (!result.Provinces.TryGetValue(targetPid, out var target)) return;
        var labels = result.Labels;
        var rgb = result.Rgb;
        int r = Math.Max(0, (int)Math.Round(radius));
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(labels.Width - 1, cx + r);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(labels.Height - 1, cy + r);
        long r2 = (long)r * r;
        for (int py = y0; py <= y1; py++)
        {
            long dy = py - cy;
            for (int px = x0; px <= x1; px++)
            {
                long dx = px - cx;
                if (dx * dx + dy * dy > r2) continue;
                int idx = py * labels.Width + px;
                int oldPid = labels.Data[idx];
                if (oldPid == targetPid) continue;
                if (oldPid < 0 || !result.Provinces.TryGetValue(oldPid, out var oldInfo)) continue;
                if (oldInfo.Kind != target.Kind) continue;
                labels.Data[idx] = targetPid;
                rgb.Data[idx] = target.Color;
                oldInfo.PixelCount--;
                target.PixelCount++;
            }
        }
    }

    /// <summary>Like PaintValue, but never *decreases* an existing pixel
    /// (so overlapping mountain-brush strokes don't erase each other).</summary>
    public static void PaintMax(GrayMap map, double x, double y, double radius, byte value)
    {
        int r = Math.Max(0, (int)Math.Round(radius));
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(map.Width - 1, cx + r);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(map.Height - 1, cy + r);
        long r2 = (long)r * r;
        for (int py = y0; py <= y1; py++)
        {
            long dy = py - cy;
            for (int px = x0; px <= x1; px++)
            {
                long dx = px - cx;
                if (dx * dx + dy * dy <= r2 && map[px, py] < value) map[px, py] = value;
            }
        }
    }

    /// <summary>Adds `delta` (may be negative, to lower instead of raise)
    /// to every pixel under a filled circle, clamped back into 0..255 -
    /// the "raise/lower terrain" sculpting brush, as opposed to
    /// PaintValue's "hard-set to an exact value" brush. Soft-edged
    /// (smoothstep falloff over the outer `1 - falloffFraction` of the
    /// radius) rather than a hard disc, since a real terrain brush reads
    /// far more natural with a feathered edge than an abrupt stop.</summary>
    public static void PaintAddClamped(GrayMap map, double x, double y, double radius, double delta, double falloffFraction = 0.55)
    {
        int r = Math.Max(0, (int)Math.Round(radius));
        int cx = (int)Math.Round(x), cy = (int)Math.Round(y);
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(map.Width - 1, cx + r);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(map.Height - 1, cy + r);
        double rf = Math.Max(r, 0.5);
        double innerR = rf * Math.Clamp(falloffFraction, 0.0, 1.0);
        for (int py = y0; py <= y1; py++)
        {
            double dy = py - cy;
            for (int px = x0; px <= x1; px++)
            {
                double dx = px - cx;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > rf) continue;
                double t = dist <= innerR ? 1.0 : Math.Clamp(1.0 - (dist - innerR) / Math.Max(rf - innerR, 1e-6), 0.0, 1.0);
                t = t * t * (3 - 2 * t); // smoothstep
                int v = map[px, py] + (int)Math.Round(delta * t);
                map[px, py] = (byte)Math.Clamp(v, 0, 255);
            }
        }
    }

    // -- flood fill / connected components --------------------------------

    /// <summary>Connected component (4-neighborhood) of `mask` containing
    /// (x, y). Returns an all-false mask if out of bounds.</summary>
    public static GrayMap FloodFillBool(GrayMap mask, int x, int y)
    {
        var outMask = GrayMap.Bool(mask.Width, mask.Height, false);
        if (!mask.InBounds(x, y)) return outMask;
        bool target = mask.GetBool(x, y);
        int w = mask.Width, h = mask.Height;
        var seen = new bool[w * h];
        var q = new Queue<int>();
        int start = y * w + x;
        seen[start] = true;
        q.Enqueue(start);
        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int px = idx % w, py = idx / w;
            outMask.Data[idx] = 255;
            foreach (var (dx, dy) in Nei4)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (seen[nidx]) continue;
                seen[nidx] = true;
                if (mask.Data[nidx] >= 128 == target) q.Enqueue(nidx);
            }
        }
        return outMask;
    }

    /// <summary>Connected-component label the area where `mask` is true and
    /// `barrier` is false. Region ids are 1..N, 0 = not part of any region.</summary>
    public static (int[] labels, int count) LabelRegions(GrayMap mask, GrayMap barrier)
    {
        int w = mask.Width, h = mask.Height;
        var effective = new bool[w * h];
        for (int i = 0; i < effective.Length; i++)
            effective[i] = mask.Data[i] >= 128 && barrier.Data[i] < 128;

        var labels = new int[w * h];
        int next = 0;
        var q = new Queue<int>();
        for (int start = 0; start < w * h; start++)
        {
            if (!effective[start] || labels[start] != 0) continue;
            next++;
            labels[start] = next;
            q.Enqueue(start);
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                int px = idx % w, py = idx / w;
                foreach (var (dx, dy) in Nei4)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int nidx = ny * w + nx;
                    if (effective[nidx] && labels[nidx] == 0)
                    {
                        labels[nidx] = next;
                        q.Enqueue(nidx);
                    }
                }
            }
        }
        return (labels, next);
    }

    /// <summary>Multi-source BFS from every true pixel touching the image
    /// border. Returns which true pixels are reachable from the border
    /// through other true pixels - i.e. "open water" vs. an enclosed lake.</summary>
    public static GrayMap BorderConnected(GrayMap mask)
    {
        int w = mask.Width, h = mask.Height;
        var reached = new bool[w * h];
        var q = new Queue<int>();

        void Seed(int x, int y)
        {
            int idx = y * w + x;
            if (mask.Data[idx] >= 128 && !reached[idx])
            {
                reached[idx] = true;
                q.Enqueue(idx);
            }
        }

        for (int x = 0; x < w; x++) { Seed(x, 0); Seed(x, h - 1); }
        for (int y = 0; y < h; y++) { Seed(0, y); Seed(w - 1, y); }

        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei4)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (!reached[nidx] && mask.Data[nidx] >= 128)
                {
                    reached[nidx] = true;
                    q.Enqueue(nidx);
                }
            }
        }

        var outMask = GrayMap.Bool(w, h, false);
        for (int i = 0; i < reached.Length; i++) if (reached[i]) outMask.Data[i] = 255;
        return outMask;
    }

    /// <summary>Multi-source BFS "Voronoi": every true pixel of `mask` gets
    /// the index (into `seeds`) of whichever seed's wavefront reaches it
    /// first. 8-connected, so cells end up organically rounded rather than
    /// diamond-shaped. -1 for pixels outside `mask` or unreached.</summary>
    public static int[] VoronoiAssign(GrayMap mask, IReadOnlyList<(int x, int y)> seeds)
    {
        int w = mask.Width, h = mask.Height;
        var labels = new int[w * h];
        Array.Fill(labels, -1);
        var q = new Queue<int>();

        for (int i = 0; i < seeds.Count; i++)
        {
            var (sx, sy) = seeds[i];
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
            int idx = sy * w + sx;
            if (mask.Data[idx] >= 128 && labels[idx] == -1)
            {
                labels[idx] = i;
                q.Enqueue(idx);
            }
        }

        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int lbl = labels[idx];
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (labels[nidx] == -1 && mask.Data[nidx] >= 128)
                {
                    labels[nidx] = lbl;
                    q.Enqueue(nidx);
                }
            }
        }
        return labels;
    }

    /// <summary>Multi-source BFS that grows every already-labeled region
    /// (label &gt;= 0) into neighboring unlabeled (-1) pixels not covered by
    /// `excludeMask`, until no reachable unlabeled pixel remains. Used
    /// after manual border-based province labeling: the border strip the
    /// user paints is deliberately treated as a barrier by LabelRegions
    /// (so it separates provinces instead of merging them), which leaves
    /// it permanently unlabeled - and therefore uncolored - unless
    /// something pulls each border pixel into whichever province ends up
    /// nearest. That is what this does; `excludeMask` should be the
    /// tile's "empty" mask so pixels genuinely outside the tile shape are
    /// never given a province.</summary>
    public static void FillUnlabeledGaps(LabelMap labels, GrayMap excludeMask)
    {
        int w = labels.Width, h = labels.Height;
        var queued = new bool[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < labels.Data.Length; i++)
        {
            if (labels.Data[i] >= 0)
            {
                queued[i] = true;
                q.Enqueue(i);
            }
        }

        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int lbl = labels.Data[idx];
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (queued[nidx] || excludeMask.Data[nidx] >= 128) continue;
                labels.Data[nidx] = lbl;
                queued[nidx] = true;
                q.Enqueue(nidx);
            }
        }
    }

    /// <summary>Like FillUnlabeledGaps, but growth is confined to `domain`:
    /// an unlabeled (-1) pixel only ever inherits a neighboring label if
    /// that neighbor is also inside `domain`, so (for example) a small
    /// island's seed grid missing it entirely by bad luck gets swallowed by
    /// the nearest LAND province rather than potentially bleeding a SEA
    /// province's label across the coastline. Used as a safety net after
    /// automatic province generation (see ProvinceMapGen.GenerateAutomatic)
    /// so a domain pixel the seed grid simply never reached can never end
    /// up unlabeled and rendering as the tile's plain black "empty"
    /// background - previously visible as small, seemingly random black
    /// patches with no obvious cause (Runde 7 feedback).</summary>
    public static void FillUnlabeledGapsWithinDomain(LabelMap labels, GrayMap domain)
    {
        int w = labels.Width, h = labels.Height;
        var queued = new bool[w * h];
        var q = new Queue<int>();
        for (int i = 0; i < labels.Data.Length; i++)
        {
            if (domain.Data[i] >= 128 && labels.Data[i] >= 0)
            {
                queued[i] = true;
                q.Enqueue(i);
            }
        }

        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int lbl = labels.Data[idx];
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (queued[nidx] || domain.Data[nidx] < 128) continue;
                labels.Data[nidx] = lbl;
                queued[nidx] = true;
                q.Enqueue(nidx);
            }
        }
    }

    /// <summary>Like VoronoiAssign, but stepping onto pixel `i` costs
    /// `cellCost[i]` (>= 1) instead of always 1, so a wavefront hesitates
    /// to push across expensive terrain - used to bias automatic province
    /// boundaries toward rivers and mountain ridgelines (in real borders,
    /// a river or a mountain range is a far more plausible dividing line
    /// than a straight cut through open flat land). Uses Dial's algorithm
    /// (a circular array of FIFO buckets indexed by distance mod
    /// max-edge-cost+1) rather than a general priority queue, since every
    /// edge cost here is a small bounded integer - keeps this close to
    /// linear time even on a full-size tile.</summary>
    public static int[] WeightedVoronoiAssign(GrayMap mask, IReadOnlyList<(int x, int y)> seeds, byte[] cellCost)
    {
        int w = mask.Width, h = mask.Height;
        int n = w * h;
        var labels = new int[n];
        Array.Fill(labels, -1);
        var dist = new int[n];
        Array.Fill(dist, int.MaxValue);

        int maxCost = 1;
        for (int i = 0; i < n; i++)
            if (mask.Data[i] >= 128) maxCost = Math.Max(maxCost, cellCost[i]);
        int numBuckets = maxCost + 1;
        var buckets = new Queue<int>[numBuckets];
        for (int i = 0; i < numBuckets; i++) buckets[i] = new Queue<int>();

        for (int i = 0; i < seeds.Count; i++)
        {
            var (sx, sy) = seeds[i];
            if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
            int idx = sy * w + sx;
            if (mask.Data[idx] >= 128 && labels[idx] == -1)
            {
                labels[idx] = i;
                dist[idx] = 0;
                buckets[0].Enqueue(idx);
            }
        }

        int d = 0, emptyStreak = 0;
        while (emptyStreak <= numBuckets)
        {
            var bucket = buckets[d % numBuckets];
            if (bucket.Count == 0)
            {
                emptyStreak++;
                d++;
                continue;
            }
            emptyStreak = 0;
            while (bucket.Count > 0)
            {
                int idx = bucket.Dequeue();
                if (dist[idx] != d) continue; // stale - a shorter path to this pixel was already found
                int lbl = labels[idx];
                int px = idx % w, py = idx / w;
                foreach (var (ddx, ddy) in Nei8)
                {
                    int nx = px + ddx, ny = py + ddy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int nidx = ny * w + nx;
                    if (mask.Data[nidx] < 128) continue;
                    int nd = d + Math.Max(1, (int)cellCost[nidx]);
                    if (nd < dist[nidx])
                    {
                        dist[nidx] = nd;
                        labels[nidx] = lbl;
                        buckets[nd % numBuckets].Enqueue(nidx);
                    }
                }
            }
            d++;
        }
        return labels;
    }

    /// <summary>Softens the straight, faceted-looking edges that
    /// VoronoiAssign/WeightedVoronoiAssign otherwise produce (both are an
    /// 8-connected graph-distance BFS, so two neighboring cells' shared
    /// border comes out as a sequence of straight octagon-like facets -
    /// this reads as "blocky" rather than organic, especially at typical
    /// province sizes). Same idea as the coastline-detail noise pass used
    /// for random-generated coastlines: a handful of small iterations
    /// where boundary pixels (a 4-neighbor has a different label) have a
    /// chance - decided by a coherent noise field, not per-pixel dice, so
    /// a whole stretch of border bulges the same way instead of
    /// flickering - to flip to whichever neighboring label sits on the
    /// other side, letting the border meander a few pixels either way
    /// each pass. 0 = untouched (the original BFS result). Applied
    /// in-place; only pixels where `domain` is true are touched, so it
    /// never reaches into sea/wasteland/other domains.</summary>
    public static void AddBorderCurviness(LabelMap labels, GrayMap domain, double curviness, int seed)
    {
        curviness = Math.Clamp(curviness, 0.0, 1.0);
        if (curviness <= 0.001) return;
        int w = labels.Width, h = labels.Height;
        int n = w * h;

        // Frozen snapshot: every decision below is made purely from this
        // ORIGINAL assignment, never from anything this pass has already
        // written - see the multi-source BFS below for why that alone
        // isn't enough (Runde 7, second attempt).
        var original = (int[])labels.Data.Clone();

        // Bigger scale (longer wavelength) at higher curviness reads as
        // smoother, more sweeping curves rather than fine wobble. Blending
        // a broad-sweep field with a finer, higher-frequency one (rather
        // than a single scale) is what makes the result read as a real
        // historical border - e.g. the Holy Roman Empire's meandering state
        // borders - instead of one uniform ripple: broad sweeps set the
        // overall shape, the fine layer adds the small local jags real
        // borders (following creeks, field boundaries, etc.) tend to have.
        // Runde 19 (user feedback: "kreativere Grenzen" wanted): doubled
        // both coefficients below so curviness=1 reaches roughly twice the
        // wavelength/depth it used to, without touching the 0-1 slider
        // range or breaking any saved BorderCurviness value. Slightly
        // raises how often the rare, still-open province-neck-severing
        // artifact (Runde 18 debugging notes) shows up at the extreme end
        // - accepted tradeoff.
        double scale = 10.0 + curviness * 44.0;
        var noiseCoarse = NoiseGen.GenerateField(w, h, scale, 2, 0.5, 2.0, seed);
        var noiseFine = NoiseGen.GenerateField(w, h, Math.Max(scale * 0.32, 4.0), 2, 0.5, 2.0, seed + 31337);

        // Maximum pixels deep the wobble may push past the original
        // boundary.
        int reach = Math.Max(1, (int)Math.Round(1.2 + curviness * 8.6)); // 1..~10 px

        // For every boundary CROSSING of the ORIGINAL labeling (a pixel
        // pair straddling two different labels), seed exactly ONE side to
        // erode into the other - never both. Runtime-Bug 11, third
        // attempt: the previous version seeded from BOTH the A-side pixel
        // AND the B-side pixel of every crossing, each independently
        // eroding "its own" label by an always-non-negative depth sampled
        // from the noise field. For small/similar depths on both sides
        // that swaps a thin band of A for B right next to an equally thin
        // band of B for A - e.g. depth 1 on both sides of a straight
        // ...AAAA|BBBB... border turns it into ...AAA[B][A]BBB..., an
        // isolated single stray pixel of each color sitting inside the
        // other's territory. That is exactly the "occasional single pixel
        // line" the user kept seeing even at low curviness, and why a
        // value of 0 (no erosion at all) was the only reliable workaround.
        //
        // The fix: process each crossing once (only from the pixel whose
        // OWN label has the smaller numeric id - the mirror pixel on the
        // other side always has the larger id there, so it is skipped,
        // guaranteeing a single canonical seed per crossing location) and
        // use the SIGN of the very same noise sample to decide which of
        // the two labels erodes at that point - never both at once. The
        // magnitude still varies smoothly 0..reach along the border's
        // length, so it still reads as a real wavy/meandering line, just
        // without the double-crossing artifact.
        var candidateLabel = new int[n];
        var dist = new int[n];
        var pushDepth = new int[n];
        Array.Fill(candidateLabel, -1);
        Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        bool Seed(int seedIdx, int myLabel, int candidate, int depth)
        {
            // A single pixel can be the seed target of more than one
            // crossing at a 3+-province junction (two different canonical
            // crossings can each pick the same neighbor as their non-
            // canonical side's altIdx) - first one found wins, same as the
            // old single-altLabel behavior; the caller falls back to the
            // OTHER side of its own crossing when this happens (see below)
            // so the crossing still gets eroded from one side rather than
            // silently staying perfectly straight.
            if (candidateLabel[seedIdx] >= 0) return false;
            candidateLabel[seedIdx] = candidate;
            dist[seedIdx] = 0;
            pushDepth[seedIdx] = depth;
            q.Enqueue(seedIdx);
            return true;
        }
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int idx = row + x;
                if (domain.Data[idx] < 128) continue;
                int lbl = original[idx];
                if (lbl < 0) continue;

                int altLabel = -1, altIdx = -1;
                if (x > 0 && domain.Data[idx - 1] >= 128) { int nl = original[idx - 1]; if (nl >= 0 && nl != lbl) { altLabel = nl; altIdx = idx - 1; } }
                if (altLabel < 0 && x < w - 1 && domain.Data[idx + 1] >= 128) { int nl = original[idx + 1]; if (nl >= 0 && nl != lbl) { altLabel = nl; altIdx = idx + 1; } }
                if (altLabel < 0 && y > 0 && domain.Data[idx - w] >= 128) { int nl = original[idx - w]; if (nl >= 0 && nl != lbl) { altLabel = nl; altIdx = idx - w; } }
                if (altLabel < 0 && y < h - 1 && domain.Data[idx + w] >= 128) { int nl = original[idx + w]; if (nl >= 0 && nl != lbl) { altLabel = nl; altIdx = idx + w; } }
                if (altLabel < 0) continue;

                // Canonical side only - the mirror pixel across this same
                // crossing has lbl/altLabel swapped, so it always fails
                // this check and is skipped, leaving exactly one seed per
                // crossing location.
                if (lbl >= altLabel) continue;

                // Sample the noise fields ONCE, right here at this
                // crossing's canonical location, and use it for BOTH the
                // direction (sign) and the depth (magnitude) of the push -
                // see the long comment above for why a single signed
                // sample per crossing (instead of one independent
                // magnitude per side) is what avoids the double-crossing
                // artifact. This value then rides along unchanged with the
                // BFS wave started from the chosen seed pixel (see below),
                // so every interior pixel's flip/no-flip decision depends
                // on WHICH original boundary crossing is nearest to it,
                // not on that interior pixel's own independent noise
                // sample (the earlier, even older bug - see Runde 7).
                double nVal = noiseCoarse[idx] * 0.7 + noiseFine[idx] * 0.3; // roughly [-1, 1]
                int depth = (int)Math.Round(reach * curviness * Math.Clamp(Math.Abs(nVal), 0.0, 1.0));
                // Fall back to the other side of THIS crossing if the
                // chosen seed pixel was already claimed by a different
                // crossing at a 3+-province junction - keeps every
                // crossing eroding from exactly one side instead of
                // occasionally staying unseeded (and perfectly straight)
                // on junction pixels.
                if (nVal >= 0)
                {
                    if (!Seed(idx, lbl, altLabel, depth))
                        Seed(altIdx, altLabel, lbl, depth);
                }
                else
                {
                    if (!Seed(altIdx, altLabel, lbl, depth))
                        Seed(idx, lbl, altLabel, depth);
                }
            }
        }
        if (q.Count == 0) return;

        // Grow each boundary pixel's "candidate other-side label" (and its
        // push depth) inward, but ONLY through pixels that still carry that
        // boundary pixel's OWN original label - i.e. only ever proposing a
        // pixel switch to its immediate neighboring province, never
        // leapfrogging into a third one further away. Multi-source BFS
        // naturally gives every pixel the nearest boundary point's values.
        while (q.Count > 0)
        {
            int idx = q.Dequeue();
            int d = dist[idx];
            if (d >= reach) continue;
            int myLabel = original[idx];
            int cand = candidateLabel[idx];
            int depth = pushDepth[idx];
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (domain.Data[nidx] < 128 || original[nidx] != myLabel) continue;
                if (dist[nidx] <= d + 1) continue;
                dist[nidx] = d + 1;
                candidateLabel[nidx] = cand;
                pushDepth[nidx] = depth;
                q.Enqueue(nidx);
            }
        }

        // Apply every flip in one pass, straight from the frozen fields
        // above: a pixel flips (deterministically, no per-pixel dice roll)
        // exactly when it sits strictly inside the push depth inherited
        // from its nearest original boundary point.
        for (int idx = 0; idx < n; idx++)
        {
            if (candidateLabel[idx] < 0) continue;
            if (dist[idx] < pushDepth[idx]) labels.Data[idx] = candidateLabel[idx];
        }

        // Runtime-Bug 11, fourth attempt: even with the single-canonical-
        // seed fix above, two DIFFERENT crossings on the same source
        // province (bordering two different neighbors) can each grow their
        // own ball into that province's territory. Nearest-seed-wins
        // multi-source BFS can then wall off part of one ball from its own
        // seed - the walled-off remainder still gets flipped (it's still
        // strictly closer to that seed than to anything else) but has no
        // edge-adjacent path back to either its seed or any pre-existing
        // pixel of its new color, so it renders as a small patch of one
        // province's color sitting fully inside another - "small blocks of
        // pixels bleeding into other provinces" (Runde 18 user report,
        // getting more frequent from curviness ~0.6 upward as `reach`
        // grows and balls are more likely to collide). Clean this up with
        // two small fixed-point passes: undo any flipped patch that never
        // reconnects to real territory of its new color, then absorb any
        // remaining sliver (a native pixel stranded when every neighbor
        // flipped away from under it) into whichever label actually
        // surrounds it.
        for (int pass = 0; pass < 12; pass++)
        {
            bool a = RevertUnattachedFlipIslands(labels, original, domain);
            bool b = AbsorbStrandedSpecks(labels, domain);
            if (!a && !b) break;
        }
    }

    /// <summary>Undoes any flipped pixel whose 4-connected same-label
    /// component (after the flip pass above) contains no "native" pixel -
    /// no pixel whose ORIGINAL label already equalled this component's
    /// label. Such a component never actually reconnects to the body of
    /// the color it was pushed into; reverting it restores every member to
    /// its original label. Returns whether anything changed, so the caller
    /// can iterate to a fixed point.</summary>
    private static bool RevertUnattachedFlipIslands(LabelMap labels, int[] original, GrayMap domain)
    {
        bool changed = false;
        int w = labels.Width, h = labels.Height, n = w * h;
        var visited = new bool[n];
        var q = new Queue<int>();
        for (int start = 0; start < n; start++)
        {
            if (visited[start] || domain.Data[start] < 128) continue;
            int lbl = labels.Data[start];
            if (lbl < 0) { visited[start] = true; continue; }
            visited[start] = true;
            q.Enqueue(start);
            var members = new List<int>();
            bool hasNative = false;
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                members.Add(idx);
                if (original[idx] == lbl) hasNative = true;
                int px = idx % w, py = idx / w;
                foreach (var (dx, dy) in Nei4)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int nidx = ny * w + nx;
                    if (visited[nidx] || domain.Data[nidx] < 128) continue;
                    if (labels.Data[nidx] != lbl) continue;
                    visited[nidx] = true;
                    q.Enqueue(nidx);
                }
            }
            if (!hasNative)
            {
                foreach (var idx in members) labels.Data[idx] = original[idx];
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Mop-up for the rarer mirror case: a pixel that never
    /// flipped can still end up completely surrounded by neighbors that
    /// flipped away, stranding it as a tiny speck of its own original
    /// color. A component of at most 4 pixels with no same-label neighbor
    /// left at all is absorbed into whichever label borders it most, since
    /// there is no "own territory" left for it to belong to. Returns
    /// whether anything changed, so the caller can iterate to a fixed
    /// point.</summary>
    private static bool AbsorbStrandedSpecks(LabelMap labels, GrayMap domain)
    {
        bool changed = false;
        int w = labels.Width, h = labels.Height, n = w * h;
        var visited = new bool[n];
        var q = new Queue<int>();
        for (int start = 0; start < n; start++)
        {
            if (visited[start] || domain.Data[start] < 128) continue;
            int lbl = labels.Data[start];
            if (lbl < 0) { visited[start] = true; continue; }
            visited[start] = true;
            q.Enqueue(start);
            var members = new List<int>();
            var borderLabelCounts = new Dictionary<int, int>();
            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                members.Add(idx);
                int px = idx % w, py = idx / w;
                foreach (var (dx, dy) in Nei4)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                    int nidx = ny * w + nx;
                    if (domain.Data[nidx] < 128) continue;
                    if (labels.Data[nidx] != lbl)
                    {
                        borderLabelCounts[labels.Data[nidx]] = borderLabelCounts.GetValueOrDefault(labels.Data[nidx]) + 1;
                        continue;
                    }
                    if (visited[nidx]) continue;
                    visited[nidx] = true;
                    q.Enqueue(nidx);
                }
            }
            if (members.Count <= 4 && borderLabelCounts.Count > 0)
            {
                int best = borderLabelCounts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
                foreach (var idx in members) labels.Data[idx] = best;
                changed = true;
            }
        }
        return changed;
    }

    // -- distance transform (2-pass chamfer 3-4, fast + good approximation
    //    of Euclidean distance) --------------------------------------------

    private const int Orth = 3;
    private const int Diag = 4;
    private const int Inf = int.MaxValue / 4;

    /// <summary>Approximate Euclidean distance (in pixels) from every pixel
    /// to the nearest true pixel of `seedMask`. True pixels of the mask get
    /// distance 0.</summary>
    public static float[] DistanceFrom(GrayMap seedMask)
    {
        int w = seedMask.Width, h = seedMask.Height;
        var dist = new int[w * h];
        for (int i = 0; i < dist.Length; i++) dist[i] = seedMask.Data[i] >= 128 ? 0 : Inf;

        // forward pass: top-left -> bottom-right
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                int best = dist[idx];
                if (x > 0) best = Math.Min(best, dist[idx - 1] + Orth);
                if (y > 0)
                {
                    best = Math.Min(best, dist[idx - w] + Orth);
                    if (x > 0) best = Math.Min(best, dist[idx - w - 1] + Diag);
                    if (x < w - 1) best = Math.Min(best, dist[idx - w + 1] + Diag);
                }
                dist[idx] = best;
            }
        }
        // backward pass: bottom-right -> top-left
        for (int y = h - 1; y >= 0; y--)
        {
            for (int x = w - 1; x >= 0; x--)
            {
                int idx = y * w + x;
                int best = dist[idx];
                if (x < w - 1) best = Math.Min(best, dist[idx + 1] + Orth);
                if (y < h - 1)
                {
                    best = Math.Min(best, dist[idx + w] + Orth);
                    if (x < w - 1) best = Math.Min(best, dist[idx + w + 1] + Diag);
                    if (x > 0) best = Math.Min(best, dist[idx + w - 1] + Diag);
                }
                dist[idx] = best;
            }
        }

        var result = new float[w * h];
        for (int i = 0; i < result.Length; i++) result[i] = dist[i] / (float)Orth;
        return result;
    }

    // -- blur (3-pass box blur ~= Gaussian, standard fast approximation) ----

    public static GrayMap GaussianBlurApprox(GrayMap map, double sigma)
    {
        if (sigma <= 0) return map.Clone();
        int boxRadius = Math.Max(1, (int)Math.Round((Math.Sqrt(12.0 * sigma * sigma / 3.0 + 1.0) - 1.0) / 2.0));
        var current = map;
        for (int pass = 0; pass < 3; pass++)
            current = BoxBlur(current, boxRadius);
        return current;
    }

    public static GrayMap BoxBlur(GrayMap map, int radius)
    {
        if (radius <= 0) return map.Clone();
        var horizontal = BoxBlurHorizontal(map, radius);
        return BoxBlurVertical(horizontal, radius);
    }

    private static GrayMap BoxBlurHorizontal(GrayMap map, int radius)
    {
        int w = map.Width, h = map.Height;
        var outMap = new GrayMap(w, h);
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * w;
            long sum = 0;
            for (int x = -radius; x <= radius; x++)
                sum += map.Data[rowStart + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                outMap.Data[rowStart + x] = (byte)(sum / (2 * radius + 1));
                int addX = Math.Clamp(x + radius + 1, 0, w - 1);
                int subX = Math.Clamp(x - radius, 0, w - 1);
                sum += map.Data[rowStart + addX] - map.Data[rowStart + subX];
            }
        }
        return outMap;
    }

    private static GrayMap BoxBlurVertical(GrayMap map, int radius)
    {
        int w = map.Width, h = map.Height;
        var outMap = new GrayMap(w, h);
        for (int x = 0; x < w; x++)
        {
            long sum = 0;
            for (int y = -radius; y <= radius; y++)
                sum += map.Data[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                outMap.Data[y * w + x] = (byte)(sum / (2 * radius + 1));
                int addY = Math.Clamp(y + radius + 1, 0, h - 1);
                int subY = Math.Clamp(y - radius, 0, h - 1);
                sum += map.Data[addY * w + x] - map.Data[subY * w + x];
            }
        }
        return outMap;
    }

    public static GrayMap ClipWhere(GrayMap map, GrayMap mask, byte lo, byte hi)
    {
        var r = map.Clone();
        for (int i = 0; i < r.Data.Length; i++)
        {
            if (mask.Data[i] >= 128)
                r.Data[i] = Math.Clamp(r.Data[i], lo, hi);
        }
        return r;
    }

    // -- float-array variants ------------------------------------------
    // Height-map generation needs continuous (non-byte-clamped) intermediate
    // fields - rounding to a byte before every blur pass would visibly
    // degrade the smooth land/sea falloff. These mirror the GrayMap blur
    // helpers above but operate on plain float[] buffers instead.

    public static float[] BoxBlurFloat(float[] data, int w, int h, int radius)
    {
        if (radius <= 0) return (float[])data.Clone();
        var horizontal = BoxBlurHorizontalFloat(data, w, h, radius);
        return BoxBlurVerticalFloat(horizontal, w, h, radius);
    }

    private static float[] BoxBlurHorizontalFloat(float[] data, int w, int h, int radius)
    {
        var outData = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * w;
            double sum = 0;
            for (int x = -radius; x <= radius; x++)
                sum += data[rowStart + Math.Clamp(x, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                outData[rowStart + x] = (float)(sum / (2 * radius + 1));
                int addX = Math.Clamp(x + radius + 1, 0, w - 1);
                int subX = Math.Clamp(x - radius, 0, w - 1);
                sum += data[rowStart + addX] - data[rowStart + subX];
            }
        }
        return outData;
    }

    private static float[] BoxBlurVerticalFloat(float[] data, int w, int h, int radius)
    {
        var outData = new float[w * h];
        for (int x = 0; x < w; x++)
        {
            double sum = 0;
            for (int y = -radius; y <= radius; y++)
                sum += data[Math.Clamp(y, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                outData[y * w + x] = (float)(sum / (2 * radius + 1));
                int addY = Math.Clamp(y + radius + 1, 0, h - 1);
                int subY = Math.Clamp(y - radius, 0, h - 1);
                sum += data[addY * w + x] - data[subY * w + x];
            }
        }
        return outData;
    }

    public static float[] GaussianBlurApproxFloat(float[] data, int w, int h, double sigma)
    {
        if (sigma <= 0) return (float[])data.Clone();
        int boxRadius = Math.Max(1, (int)Math.Round((Math.Sqrt(12.0 * sigma * sigma / 3.0 + 1.0) - 1.0) / 2.0));
        var current = data;
        for (int pass = 0; pass < 3; pass++) current = BoxBlurFloat(current, w, h, boxRadius);
        return current;
    }

    /// <summary>Gaussian-blur `field` using only the pixels where `mask` is
    /// true, without letting the (possibly garbage/zero) values outside the
    /// mask leak into the average - the "normalized convolution" trick, so a
    /// blur near a coastline never pulls land values into the sea field or
    /// vice versa.</summary>
    public static float[] MaskedGaussianBlur(float[] field, GrayMap mask, double sigma)
    {
        int w = mask.Width, h = mask.Height;
        if (sigma <= 0) return (float[])field.Clone();

        var weighted = new float[w * h];
        var weight = new float[w * h];
        for (int i = 0; i < field.Length; i++)
        {
            float m = mask.Data[i] >= 128 ? 1f : 0f;
            weighted[i] = field[i] * m;
            weight[i] = m;
        }
        weighted = GaussianBlurApproxFloat(weighted, w, h, sigma);
        weight = GaussianBlurApproxFloat(weight, w, h, sigma);

        var result = new float[w * h];
        for (int i = 0; i < result.Length; i++)
            result[i] = weight[i] > 1e-6f ? weighted[i] / Math.Max(weight[i], 1e-6f) : field[i];
        return result;
    }

    // -- multi-component selection, max-filter/dilation, ridge widening,
    //    and shortest-path-through-a-mask (added for the "import an
    //    external heightmap" and expanded "random generation" features) ---

    /// <summary>Keeps only the single largest 4-connected true-component.
    /// Thin wrapper over LargestComponents(mask, 1) kept for callers that
    /// only ever wanted "one continent".</summary>
    public static GrayMap LargestComponent(GrayMap mask) => LargestComponents(mask, 1);

    /// <summary>Keeps the `keep` largest 4-connected true-components of
    /// `mask` (by pixel count), clearing every smaller/other component -
    /// generalizes LargestComponent to support a requested island count
    /// during random tile generation (keep=1 reproduces the old "single
    /// continent" behavior).</summary>
    public static GrayMap LargestComponents(GrayMap mask, int keep)
    {
        keep = Math.Max(1, keep);
        var (labels, n) = LabelRegions(mask, GrayMap.Bool(mask.Width, mask.Height, false));
        var outMask = GrayMap.Bool(mask.Width, mask.Height, false);
        if (n == 0) return outMask;

        var counts = new int[n + 1];
        foreach (var l in labels) if (l > 0) counts[l]++;
        var keepLabels = new HashSet<int>(
            Enumerable.Range(1, n).OrderByDescending(i => counts[i]).Take(keep));

        for (int i = 0; i < labels.Length; i++)
            if (keepLabels.Contains(labels[i])) outMask.Data[i] = 255;
        return outMask;
    }

    /// <summary>Separable square max-filter (radius r -> a (2r+1)x(2r+1)
    /// neighborhood), i.e. grayscale dilation. Used both to literally
    /// dilate a boolean mask (a 0/255 GrayMap's max-filtered result is
    /// exactly its dilation) and to widen/thicken a sketched height-map
    /// ridge line before blurring it.</summary>
    public static GrayMap MaxFilterSquare(GrayMap map, int radius)
    {
        if (radius <= 0) return map.Clone();
        return MaxFilterVertical(MaxFilterHorizontal(map, radius), radius);
    }

    /// <summary>Dilates a boolean (0/255) mask by `radius` pixels. Thin
    /// semantic alias over MaxFilterSquare.</summary>
    public static GrayMap DilateBool(GrayMap mask, int radius) => MaxFilterSquare(mask, radius);

    private static GrayMap MaxFilterHorizontal(GrayMap map, int radius)
    {
        int w = map.Width, h = map.Height;
        var outMap = new GrayMap(w, h);
        for (int y = 0; y < h; y++)
        {
            int rowStart = y * w;
            for (int x = 0; x < w; x++)
            {
                byte best = 0;
                int x0 = Math.Max(0, x - radius), x1 = Math.Min(w - 1, x + radius);
                for (int xx = x0; xx <= x1; xx++)
                    if (map.Data[rowStart + xx] > best) best = map.Data[rowStart + xx];
                outMap.Data[rowStart + x] = best;
            }
        }
        return outMap;
    }

    private static GrayMap MaxFilterVertical(GrayMap map, int radius)
    {
        int w = map.Width, h = map.Height;
        var outMap = new GrayMap(w, h);
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                byte best = 0;
                int y0 = Math.Max(0, y - radius), y1 = Math.Min(h - 1, y + radius);
                for (int yy = y0; yy <= y1; yy++)
                    if (map.Data[yy * w + x] > best) best = map.Data[yy * w + x];
                outMap.Data[y * w + x] = best;
            }
        }
        return outMap;
    }

    /// <summary>Per-pixel "how different is this from its 8 neighbors"
    /// (max absolute difference to each of the 8 immediate neighbors,
    /// border pixels clamped to stay in-bounds). Used to find sketched
    /// ridge lines in an imported heightmap: a hand-drawn line reads as a
    /// short, sharp jump in an otherwise smoothly-varying elevation field.</summary>
    public static GrayMap EdgeStrength(GrayMap map)
    {
        int w = map.Width, h = map.Height;
        var outMap = new GrayMap(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                byte c = map.Data[y * w + x];
                int best = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = Math.Clamp(y + dy, 0, h - 1);
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = Math.Clamp(x + dx, 0, w - 1);
                        int diff = Math.Abs(c - map.Data[ny * w + nx]);
                        if (diff > best) best = diff;
                    }
                }
                outMap.Data[y * w + x] = (byte)Math.Clamp(best, 0, 255);
            }
        }
        return outMap;
    }

    /// <summary>
    /// Turns thin hand-sketched ridge lines in a grayscale heightmap into
    /// wide, soft mountain ridges - built for the "import an unfinished
    /// heightmap" feature, directly implementing the reference comment
    /// ("blur the lines whenever the difference of color hues is greater
    /// than [edgeThreshold], mostly affecting the areas around the
    /// lines"), where "hue" in that note turned out (checked against the
    /// actual reference image) to mean plain grayscale brightness, not
    /// HSV hue.
    ///
    /// Pipeline: (1) find edge pixels (EdgeStrength &gt; edgeThreshold,
    /// skipping any `excludeMask` pixel - e.g. a known sea mask, so the
    /// coastline itself is never mistaken for a sketched ridge); (2)
    /// widen/thicken the raw line values with a max-filter of
    /// `lineWidenRadius`; (3) dilate the edge mask by `corridorRadius` to
    /// get the "areas around the lines" corridor; (4) heavily blur the
    /// thickened field; (5) blend the blurred result into the corridor
    /// with a soft feathered edge, leaving everything outside it (and any
    /// excluded pixel) completely untouched.
    /// </summary>
    public static GrayMap WidenAndBlurRidgeLines(GrayMap heightField, GrayMap? excludeMask, int edgeThreshold, int lineWidenRadius, int corridorRadius, double blurSigma)
    {
        int w = heightField.Width, h = heightField.Height;
        var edge = EdgeStrength(heightField);
        var edgeMask = GrayMap.Bool(w, h, false);
        for (int i = 0; i < edge.Data.Length; i++)
            if (edge.Data[i] > edgeThreshold) edgeMask.Data[i] = 255;
        if (excludeMask != null)
            edgeMask = GrayMap.And(edgeMask, GrayMap.Not(excludeMask));

        var thickened = MaxFilterSquare(heightField, Math.Max(0, lineWidenRadius));
        // Runde 19 (user feedback: raising "Blur strength" alone didn't
        // make mountains read as soft as the coastline, no matter how
        // high). Root cause: corridorRadius controls how FAR the blur's
        // influence reaches, blurSigma only how SMOOTH it looks inside
        // that fixed reach - past the corridor+feather band the raw,
        // unblurred sketch value took over abruptly, so a bigger sigma
        // alone just made a smoother patch inside the same hard-edged
        // area. Widening the corridor together with sigma (a real
        // Gaussian's useful influence radius scales with its sigma) makes
        // the "Blur strength" slider actually widen the soft transition,
        // not just its internal smoothness.
        int effectiveCorridorRadius = Math.Max(Math.Max(0, corridorRadius), (int)Math.Round(blurSigma * 2.5));
        var corridor = DilateBool(edgeMask, effectiveCorridorRadius);
        if (excludeMask != null)
            for (int i = 0; i < corridor.Data.Length; i++)
                if (excludeMask.Data[i] >= 128) corridor.Data[i] = 0;

        var blurred = GaussianBlurApprox(thickened, Math.Max(blurSigma, 0.5));
        float[] distOutside = DistanceFrom(corridor);
        double feather = Math.Max(effectiveCorridorRadius * 0.6, 4.0);

        var result = heightField.Clone();
        for (int i = 0; i < result.Data.Length; i++)
        {
            if (excludeMask != null && excludeMask.Data[i] >= 128) continue;
            double alpha = Math.Clamp(1.0 - distOutside[i] / feather, 0.0, 1.0);
            if (alpha <= 0.0) continue;
            double v = heightField.Data[i] * (1 - alpha) + blurred.Data[i] * alpha;
            result.Data[i] = (byte)Math.Clamp(Math.Round(v), 0, 255);
        }
        return result;
    }

    /// <summary>Multi-source BFS shortest path through `passable` pixels,
    /// starting from any pixel of `fromMask`, ending as soon as a pixel
    /// adjacent (8-connected) to `toMask` is reached. Returns the path
    /// (passable cells only, from-endpoint first) or null if `toMask` is
    /// unreachable through `passable` pixels alone. Used to carve the
    /// shortest practical water channel between an inland sea/lake and
    /// the open ocean during random tile generation.</summary>
    public static List<(int x, int y)>? ShortestPathThroughMask(GrayMap fromMask, GrayMap toMask, GrayMap passable)
    {
        int w = passable.Width, h = passable.Height;
        int n = w * h;
        var prev = new int[n];
        Array.Fill(prev, -2); // -2 = unvisited, -1 = a start pixel
        var q = new Queue<int>();
        for (int i = 0; i < n; i++)
        {
            if (fromMask.Data[i] >= 128 && passable.Data[i] >= 128)
            {
                prev[i] = -1;
                q.Enqueue(i);
            }
        }

        int found = -1;
        while (q.Count > 0 && found < 0)
        {
            int idx = q.Dequeue();
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                if (toMask.Data[ny * w + nx] >= 128) { found = idx; break; }
            }
            if (found >= 0) break;
            foreach (var (dx, dy) in Nei4)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (prev[nidx] == -2 && passable.Data[nidx] >= 128)
                {
                    prev[nidx] = idx;
                    q.Enqueue(nidx);
                }
            }
        }
        if (found < 0) return null;

        var path = new List<(int, int)>();
        int cur = found;
        while (cur != -1)
        {
            path.Add((cur % w, cur / w));
            cur = prev[cur];
        }
        path.Reverse();
        return path;
    }
}
