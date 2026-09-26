using System;
using System.Collections.Generic;

namespace RnwTileGenerator.Core;

/// <summary>
/// Automatic height map (_h.bmp) generation. Direct port of the original
/// Python "rnw/heightmap.py" module (which used numpy/scipy), now built on
/// RasterOps's chamfer distance transform and box-blur-based Gaussian
/// approximation instead - no native dependency beyond the .NET runtime.
///
/// Strategy, following the guide's own recipe of layered borders + blur:
///
/// 1. Sea pixels fade from HeightShallowMax right at the coast down to
///    HeightDeepOcean out in the open water.
/// 2. Land pixels start at HeightLandBase right at the coast and rise
///    toward the interior.
/// 3. The user's painted "mountains" mask adds extra height on top of the
///    land base, and is blurred so ridges don't look artificially sharp.
/// 4. Everything is blurred a bit more (separately for land and for sea, so
///    the blur never crosses the coastline and reintroduces the risky
///    94/95 band), then the coast is re-stamped to a clean
///    HeightLandBase / HeightShallowMax step, matching the guide's explicit
///    advice to avoid 94 and 95 where possible.
/// 5. River mouths are dipped below HeightRiverMouthMax so they don't look
///    like they end abruptly on visibly-dry water.
///
/// The result is a reasonable starting point, not a finished piece of art -
/// the guide is explicit that hand-tuning after seeing the tile in-game is
/// normal. The GUI also exposes a manual height brush for exactly that
/// (see ApplyManualHeightBrush).
/// </summary>
public static class HeightMapGen
{
    public sealed class Options
    {
        public double LandRiseDistance = 60.0;
        public double LandInteriorHeight = 150.0;
        public double MountainBoost = 85.0;
        public double SeaFadeDistance = 40.0;
        /// <summary>Blur applied to the coastline-to-interior height
        /// gradient itself (both the land and sea fields, each masked so
        /// the blur never crosses the coastline) - lower keeps a crisper,
        /// more defined coastline transition.</summary>
        public double BlurCoastlines = 2.5;
        /// <summary>Blur applied to the painted mountain-intensity mask
        /// before it's added on top of the land height field - higher
        /// softens ridges into broader, gentler massifs instead of sharp
        /// spikes. Applied separately from BlurCoastlines so a tile can
        /// have soft, rounded mountains without also smearing out its
        /// coastline (or vice versa).</summary>
        public double BlurMountains = 4.0;
        public int RiverMouthRadius = 3;
    }

    /// <summary>
    /// Build an 8-bit height map.
    /// landMask: true = above water. mountainMask: byte intensity 0..255,
    /// treated as 0..1 extra-elevation strength (as painted by the mountain
    /// brush - see RasterOps.PaintMax). riverMouths: optional pixel
    /// coordinates where a river meets water; those spots get pushed below
    /// HeightRiverMouthMax so they don't look landlocked.
    /// </summary>
    public static GrayMap Generate(
        GrayMap landMask,
        GrayMap mountainMask,
        Options? options = null,
        IReadOnlyList<(int x, int y)>? riverMouths = null,
        Random? rng = null)
    {
        options ??= new Options();
        rng ??= new Random();

        int w = landMask.Width, h = landMask.Height;
        var seaMask = GrayMap.Not(landMask);

        // Distance (in px) from each pixel to the nearest sea pixel (used on
        // the land side, growing inland) and to the nearest land pixel
        // (used on the sea side, growing seaward).
        float[] distIntoLand = RasterOps.DistanceFrom(seaMask);
        float[] distIntoSea = RasterOps.DistanceFrom(landMask);

        int n = w * h;
        var landField = new float[n];
        var seaField = new float[n];

        double landRise = Math.Max(options.LandRiseDistance, 1e-6);
        double seaFade = Math.Max(options.SeaFadeDistance, 1e-6);

        for (int i = 0; i < n; i++)
        {
            double landT = Math.Clamp(distIntoLand[i] / landRise, 0.0, 1.0);
            landField[i] = (float)(Constants.HeightLandBase + landT * (options.LandInteriorHeight - Constants.HeightLandBase));

            double seaT = Math.Clamp(distIntoSea[i] / seaFade, 0.0, 1.0);
            seaField[i] = (float)(Constants.HeightShallowMax - seaT * (Constants.HeightShallowMax - Constants.HeightDeepOcean));
        }

        // Mountain mask (0..1 intensity), blurred so ridges aren't sharp,
        // added on top of the land base field.
        var mtn = new float[n];
        for (int i = 0; i < n; i++) mtn[i] = mountainMask.Data[i] / 255f;
        var mtnBlurred = RasterOps.GaussianBlurApproxFloat(mtn, w, h, Math.Max(options.BlurMountains, 1.5));
        for (int i = 0; i < n; i++) landField[i] += mtnBlurred[i] * (float)options.MountainBoost;

        // Smooth, but never across the coastline (normalized convolution
        // keeps land values from leaking into the sea field and back).
        landField = RasterOps.MaskedGaussianBlur(landField, landMask, options.BlurCoastlines);
        seaField = RasterOps.MaskedGaussianBlur(seaField, seaMask, options.BlurCoastlines);

        var height = new float[n];
        for (int i = 0; i < n; i++)
            height[i] = landMask.Data[i] >= 128 ? landField[i] : seaField[i];

        // Re-clip into the safe bands and re-stamp a crisp 1px coastline,
        // per the guide's own layer recipe (land border = HeightLandBase,
        // sea border = HeightShallowMax) so the risky 94/95 values never
        // appear.
        for (int i = 0; i < n; i++)
        {
            if (landMask.Data[i] >= 128)
                height[i] = Math.Clamp(height[i], Constants.HeightLandBase, Constants.HeightMaxByte);
            else
                height[i] = Math.Clamp(height[i], Constants.HeightDeepOcean, Constants.HeightShallowMax);
        }

        for (int i = 0; i < n; i++)
        {
            bool isLand = landMask.Data[i] >= 128;
            if (isLand && distIntoLand[i] <= 1f) height[i] = Constants.HeightLandBase;
            else if (!isLand && distIntoSea[i] <= 1f) height[i] = Constants.HeightShallowMax;
        }

        if (riverMouths != null && riverMouths.Count > 0)
        {
            double lo = 75.0;
            double hi = Constants.HeightRiverMouthMax - 1;
            foreach (var (mx, my) in riverMouths)
            {
                if (mx < 0 || mx >= w || my < 0 || my >= h) continue;
                int r = options.RiverMouthRadius;
                int x0 = Math.Max(0, mx - r), x1 = Math.Min(w - 1, mx + r);
                int y0 = Math.Max(0, my - r), y1 = Math.Min(h - 1, my + r);
                double r2 = (double)r * r;
                for (int y = y0; y <= y1; y++)
                {
                    double dy = y - my;
                    for (int x = x0; x <= x1; x++)
                    {
                        double dx = x - mx;
                        if (dx * dx + dy * dy > r2) continue;
                        int idx = y * w + x;
                        if (seaMask.Data[idx] < 128) continue;
                        height[idx] = (float)(lo + rng.NextDouble() * (hi - lo));
                    }
                }
            }
        }

        var outMap = new GrayMap(w, h);
        for (int i = 0; i < n; i++)
            outMap.Data[i] = (byte)Math.Clamp(Math.Round(height[i]), 0, 255);
        return outMap;
    }

    /// <summary>
    /// Blend `height` toward `targetHeight` under `brushMask` (byte
    /// intensity 0..255), used by the interactive height brush in the GUI.
    /// Returns a new GrayMap; does not mutate the input.
    /// </summary>
    public static GrayMap ApplyManualHeightBrush(GrayMap height, GrayMap brushMask, byte targetHeight, double strength = 1.0)
    {
        strength = Math.Clamp(strength, 0.0, 1.0);
        var outMap = new GrayMap(height.Width, height.Height);
        for (int i = 0; i < height.Data.Length; i++)
        {
            double blend = Math.Clamp(brushMask.Data[i] / 255.0, 0.0, 1.0) * strength;
            double v = height.Data[i] * (1 - blend) + targetHeight * blend;
            outMap.Data[i] = (byte)Math.Clamp(Math.Round(v), 0, 255);
        }
        return outMap;
    }
}
