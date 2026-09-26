using System;
using System.Collections.Generic;

namespace RnwTileGenerator.Core;

/// <summary>
/// D8 flow direction / flow accumulation (priority flood) on a plain grid.
/// Used by CoarseRiverGen - see docs/superpowers/specs/2026-09-18-coarse-grid-rivers-design.md.
/// </summary>
public static class FlowNetworkGen
{
    private static readonly (int dx, int dy)[] Nei8 =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    /// <summary>Priority-flood depression filling merged with D8 flow direction (Barnes et
    /// al.): a min-heap seeded with every water cell; each popped cell P discovers its
    /// unvisited neighbours N, sets filled[N] = max(height[N], filled[P]) and next[N] = P.
    /// `next[]` is the flood's own parent pointer, so it is acyclic by construction - a
    /// separate steepest-descent re-scan can cycle on flat plateaus. `popOrder` is the
    /// dequeue order (a valid root-to-leaf order of the tree). Water cells
    /// (land[i] == false) keep next = -1. Runs on the coarse river grid (float mean
    /// heights have no 8-bit ties).</summary>
    public static (int[] next, List<int> popOrder) FillDepressions(bool[] land, float[] height, int w, int h)
    {
        int n = w * h;
        var filled = new float[n];
        var next = new int[n];
        Array.Fill(next, -1);
        var visited = new bool[n];
        var popOrder = new List<int>(n);
        var pq = new PriorityQueue<int, float>();
        for (int i = 0; i < n; i++)
        {
            if (land[i]) continue;
            filled[i] = height[i];
            visited[i] = true;
            pq.Enqueue(i, filled[i]);
        }
        while (pq.Count > 0)
        {
            int idx = pq.Dequeue();
            popOrder.Add(idx);
            int px = idx % w, py = idx / w;
            foreach (var (dx, dy) in Nei8)
            {
                int nx = px + dx, ny = py + dy;
                if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                int nidx = ny * w + nx;
                if (visited[nidx]) continue;
                visited[nidx] = true;
                filled[nidx] = Math.Max(height[nidx], filled[idx]);
                next[nidx] = idx;
                pq.Enqueue(nidx, filled[nidx]);
            }
        }
        return (next, popOrder);
    }

    /// <summary>Flow accumulation: accum[p] = 1 + accum of every land cell draining into p.
    /// Processes `popOrder` in REVERSE (children before parents); sorting by height is NOT
    /// safe, because ties on plateaus break the parent-before-child order.</summary>
    public static int[] Accumulate(bool[] land, int[] next, List<int> popOrder)
    {
        var accum = new int[land.Length];
        for (int i = 0; i < accum.Length; i++)
            if (land[i]) accum[i] = 1;
        for (int k = popOrder.Count - 1; k >= 0; k--)
        {
            int idx = popOrder[k];
            if (!land[idx]) continue;
            int downstream = next[idx];
            if (downstream >= 0 && land[downstream]) accum[downstream] += accum[idx];
        }
        return accum;
    }
}
