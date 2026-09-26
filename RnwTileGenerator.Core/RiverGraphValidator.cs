using System;
using System.Collections.Generic;

namespace RnwTileGenerator.Core;

/// <summary>Result of <see cref="RiverGraphValidator.Analyze"/>.</summary>
public sealed class RiverGraphReport
{
    public int RiverPixels;
    public int Components;
    /// <summary>Sum over components of edges - (nodes - 1); 0 = every component is a tree.</summary>
    public int ExcessEdges;
    public int MaxDegree;
    public int Degree4Pixels;
    /// <summary>Up to 5 pixels where a cycle closes or a pixel has degree >= 4.</summary>
    public List<(int x, int y)> Offenders = new();
    /// <summary>The game crashes with "Circular river" unless this is true (vanilla
    /// river bitmaps: max degree 3, every component a tree).</summary>
    public bool IsAcyclic => ExcessEdges == 0 && MaxDegree <= 3;
}

/// <summary>Checks a river index raster (palette index &lt; 254 = river pixel) for the
/// pixel-graph properties the game needs: 4-connected components must be trees
/// with no pixel of degree 4.</summary>
public static class RiverGraphValidator
{
    private const int MaxOffenders = 5;

    public static RiverGraphReport Analyze(byte[] riverIdx, int w, int h)
    {
        var report = new RiverGraphReport();
        int n = w * h;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int a)
        {
            while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; }
            return a;
        }
        bool IsRiver(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && riverIdx[y * w + x] < Constants.RiverPalSeaBg;
        void AddOffender(int x, int y)
        {
            if (report.Offenders.Count < MaxOffenders) report.Offenders.Add((x, y));
        }

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!IsRiver(x, y)) continue;
                report.RiverPixels++;
                int deg = 0;
                if (IsRiver(x - 1, y)) deg++;
                if (IsRiver(x + 1, y)) deg++;
                if (IsRiver(x, y - 1)) deg++;
                if (IsRiver(x, y + 1)) deg++;
                if (deg > report.MaxDegree) report.MaxDegree = deg;
                if (deg >= 4) { report.Degree4Pixels++; AddOffender(x, y); }

                // Each edge is visited once (right and down neighbour); an edge that joins two
                // pixels already in the same set closes a cycle.
                foreach (var (nx, ny) in new[] { (x + 1, y), (x, y + 1) })
                {
                    if (!IsRiver(nx, ny)) continue;
                    int a = Find(y * w + x), b = Find(ny * w + nx);
                    if (a == b) { report.ExcessEdges++; AddOffender(x, y); }
                    else parent[a] = b;
                }
            }
        }

        for (int i = 0; i < n; i++)
            if (riverIdx[i] < Constants.RiverPalSeaBg && Find(i) == i) report.Components++;
        return report;
    }
}
