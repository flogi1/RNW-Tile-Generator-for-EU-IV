using System;

namespace RnwTileGenerator.Core;

/// <summary>The land mask and height field reduced to cells of k x k pixels, used by the
/// coarse river generator. A cell is land only when every pixel in it is land, so a cell
/// centre is always inland. Height is the float mean, which removes the 8-bit plateau
/// ties of the pixel height map. Remainder pixels at the right/bottom edge (when the
/// tile size is not a multiple of k) belong to no cell.</summary>
public sealed class CoarseGrid
{
    public int K { get; }
    public int Cw { get; }
    public int Ch { get; }
    public bool[] Land { get; }
    public float[] Height { get; }

    private CoarseGrid(int k, int cw, int ch)
    {
        K = k;
        Cw = cw;
        Ch = ch;
        Land = new bool[cw * ch];
        Height = new float[cw * ch];
    }

    public static CoarseGrid Build(GrayMap landMask, GrayMap height, int k)
    {
        int w = landMask.Width;
        var g = new CoarseGrid(k, w / k, landMask.Height / k);
        for (int cy = 0; cy < g.Ch; cy++)
        {
            for (int cx = 0; cx < g.Cw; cx++)
            {
                bool allLand = true;
                double sum = 0;
                for (int y = 0; y < k; y++)
                {
                    int row = (cy * k + y) * w + cx * k;
                    for (int x = 0; x < k; x++)
                    {
                        if (landMask.Data[row + x] < 128) allLand = false;
                        sum += height.Data[row + x];
                    }
                }
                g.Land[cy * g.Cw + cx] = allLand;
                g.Height[cy * g.Cw + cx] = (float)(sum / (k * k) + TieBreakNoise(cx, cy));
            }
        }
        return g;
    }

    /// <summary>Amplitude (in height units, 0-255 scale) of the smooth noise added to every
    /// land cell so that perfectly flat plateaus do not drain in a wavefront comb. Far below
    /// real relief, so it only decides where the terrain itself has no opinion.</summary>
    private const double TieBreakAmplitude = 1.0;

    private static double TieBreakNoise(int cx, int cy) =>
        TieBreakAmplitude * NoiseGen.Fractal2D(cx / 8.0, cy / 8.0, 3, 0.5, 2.0, 7919);

    public (int x, int y) CenterPixel(int cell) => ((cell % Cw) * K + K / 2, (cell / Cw) * K + K / 2);
}
