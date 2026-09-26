using System;

namespace RnwTileGenerator.Core;

/// <summary>
/// From-scratch, dependency-free fractal value noise, used by
/// TileProject.GenerateRandom to shape random landmasses. No third-party
/// noise library exists in this project's zero-NuGet dependency set, so
/// this is a small hand-rolled implementation: a hash-based value noise
/// (every integer lattice point gets a pseudo-random value derived purely
/// from its coordinates + a seed via bit-mixing, so there is no shared
/// mutable permutation table to seed/reset - safe to call from anywhere),
/// smoothly interpolated with Perlin's quintic fade curve, then summed
/// across octaves (standard fractal Brownian motion) for natural-looking
/// multi-scale detail.
/// </summary>
public static class NoiseGen
{
    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2.0 - 1.0; // [-1, 1]
        }
    }

    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>Smooth value noise at a continuous (x, y), roughly in
    /// [-1, 1]. The integer part of x/y selects the lattice cell.</summary>
    public static double ValueNoise2D(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = x0 + 1, y1 = y0 + 1;
        double sx = Fade(x - x0), sy = Fade(y - y0);

        double n00 = Hash(x0, y0, seed), n10 = Hash(x1, y0, seed);
        double n01 = Hash(x0, y1, seed), n11 = Hash(x1, y1, seed);

        double ix0 = Lerp(n00, n10, sx);
        double ix1 = Lerp(n01, n11, sx);
        return Lerp(ix0, ix1, sy);
    }

    /// <summary>Sum of `octaves` layers of ValueNoise2D at doubling
    /// frequency (lacunarity) and shrinking amplitude (persistence),
    /// normalized back to roughly [-1, 1] regardless of octave count.</summary>
    public static double Fractal2D(double x, double y, int octaves, double persistence, double lacunarity, int seed)
    {
        double total = 0, amplitude = 1, frequency = 1, maxAmp = 0;
        for (int i = 0; i < octaves; i++)
        {
            total += ValueNoise2D(x * frequency, y * frequency, seed + i * 1013) * amplitude;
            maxAmp += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return maxAmp > 1e-9 ? total / maxAmp : 0;
    }

    /// <summary>Fills a w*h field of fractal noise, one call per pixel.
    /// `scale` is the lattice spacing in pixels for the base (lowest
    /// frequency) octave - bigger scale = larger, smoother features.
    /// Optionally domain-warps the sampling position with a second,
    /// independent noise field (warpStrength in pixels) for the rougher,
    /// less coherent, "obscure" landmass shapes at high obscurity levels.</summary>
    public static float[] GenerateField(int w, int h, double scale, int octaves, double persistence, double lacunarity, int seed, double warpStrength = 0)
    {
        scale = Math.Max(scale, 1e-3);
        var field = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double sx = x, sy = y;
                if (warpStrength > 0)
                {
                    double wx = Fractal2D(x / (scale * 1.7), y / (scale * 1.7), 2, 0.5, 2.0, seed + 4001);
                    double wy = Fractal2D(x / (scale * 1.7), y / (scale * 1.7), 2, 0.5, 2.0, seed + 8002);
                    sx += wx * warpStrength;
                    sy += wy * warpStrength;
                }
                field[y * w + x] = (float)Fractal2D(sx / scale, sy / scale, octaves, persistence, lacunarity, seed);
            }
        }
        return field;
    }
}
