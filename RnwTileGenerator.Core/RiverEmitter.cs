using System;
using System.Collections.Generic;
using System.Linq;

namespace RnwTileGenerator.Core;

/// <summary>Curve helpers for the coarse river generator.</summary>
public static class RiverGeometry
{
    /// <summary>Chaikin corner cutting. Keeps the first and last point; never overshoots
    /// the control polygon. Lists with fewer than 3 points are returned unchanged.</summary>
    public static List<(double x, double y)> Chaikin(IReadOnlyList<(double x, double y)> pts, int iterations)
    {
        var p = new List<(double x, double y)>(pts);
        for (int it = 0; it < iterations && p.Count >= 3; it++)
        {
            var q = new List<(double x, double y)>(p.Count * 2 + 2) { p[0] };
            for (int i = 0; i < p.Count - 1; i++)
            {
                var a = p[i];
                var b = p[i + 1];
                q.Add((0.75 * a.x + 0.25 * b.x, 0.75 * a.y + 0.25 * b.y));
                q.Add((0.25 * a.x + 0.75 * b.x, 0.25 * a.y + 0.75 * b.y));
            }
            q.Add(p[^1]);
            p = q;
        }
        return p;
    }

    /// <summary>Rounds half up (floor(v + 0.5)); banker's rounding made odd cell sizes jitter.</summary>
    public static List<(int x, int y)> RoundToPixels(IReadOnlyList<(double x, double y)> pts) =>
        pts.Select(p => (x: (int)Math.Floor(p.x + 0.5), y: (int)Math.Floor(p.y + 0.5))).ToList();
}

/// <summary>Tile-wide set of drawn river pixels plus the rule that keeps the river pixel
/// graph a forest: a new pixel may not be placed already and may not be 4-adjacent to
/// any placed pixel other than its immediate predecessor in the same segment. Because
/// only accepted pixels are ever placed, no cycle can arise.</summary>
public sealed class RiverEmitter
{
    private readonly bool[] _placed;
    private readonly int _w, _h;

    public RiverEmitter(int w, int h)
    {
        _w = w;
        _h = h;
        _placed = new bool[w * h];
    }

    /// <summary>Marks every river pixel (palette index &lt; 254) of an existing river raster as
    /// placed, so newly drawn rivers keep clear of it (used when regenerating on top of a
    /// river layer imported from an existing tile).</summary>
    public void MarkExisting(byte[] riverIdx)
    {
        int n = Math.Min(riverIdx.Length, _placed.Length);
        for (int i = 0; i < n; i++)
            if (riverIdx[i] < Constants.RiverPalSeaBg) _placed[i] = true;
    }

    public bool IsPlaced(int x, int y) => x >= 0 && y >= 0 && x < _w && y < _h && _placed[y * _w + x];

    /// <summary>How many leading pixels of <paramref name="path"/> can be drawn without
    /// breaking the rule. Nothing is committed. When <paramref name="firstIsShared"/> is
    /// true, path[0] must already be placed (a junction pixel on the host river) and is
    /// accepted as is unless it already has 3 placed neighbours (0 is returned then).</summary>
    public int ValidPrefixLength(IReadOnlyList<(int x, int y)> path, bool firstIsShared)
    {
        var local = new HashSet<int>();
        int n = 0;
        for (int i = 0; i < path.Count; i++)
        {
            var (x, y) = path[i];
            if (x < 0 || y < 0 || x >= _w || y >= _h) break;
            int key = y * _w + x;
            if (i == 0 && firstIsShared)
            {
                if (!_placed[key]) break;
                // A junction pixel that already has 3 placed neighbours would get degree 4
                // (no cycle, but the game rejects it; vanilla files have none).
                if (PlacedNeighbourCount(x, y) >= 3) break;
                local.Add(key);
                n = 1;
                continue;
            }
            if (_placed[key] || local.Contains(key)) break;
            int predKey = i > 0 ? path[i - 1].y * _w + path[i - 1].x : -1;
            if (TouchesForeign(x, y, predKey, local)) break;
            local.Add(key);
            n = i + 1;
        }
        return n;
    }

    /// <summary>Marks the pixels as placed. Pass the already trimmed list (only the valid prefix).</summary>
    public void Commit(IReadOnlyList<(int x, int y)> path, bool firstIsShared)
    {
        for (int i = firstIsShared ? 1 : 0; i < path.Count; i++) _placed[path[i].y * _w + path[i].x] = true;
    }

    private int PlacedNeighbourCount(int x, int y) =>
        (IsPlaced(x + 1, y) ? 1 : 0) + (IsPlaced(x - 1, y) ? 1 : 0) + (IsPlaced(x, y + 1) ? 1 : 0) + (IsPlaced(x, y - 1) ? 1 : 0);

    private bool TouchesForeign(int x, int y, int predKey, HashSet<int> local)
    {
        for (int d = 0; d < 4; d++)
        {
            int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
            int ny = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
            if (nx < 0 || ny < 0 || nx >= _w || ny >= _h) continue;
            int nk = ny * _w + nx;
            if (nk == predKey) continue;
            if (_placed[nk] || local.Contains(nk)) return true;
        }
        return false;
    }
}
