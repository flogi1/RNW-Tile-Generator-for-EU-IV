using System;
using System.Collections.Generic;

namespace RnwTileGenerator.Core;

public static class ColorMath
{
    /// <summary>h, s, v all in [0, 1].</summary>
    public static Rgb HsvToRgb(double h, double s, double v)
    {
        if (s <= 0.0)
        {
            byte gray = (byte)Math.Round(v * 255);
            return new Rgb(gray, gray, gray);
        }
        double hh = (h - Math.Floor(h)) * 6.0;
        int i = (int)Math.Floor(hh) % 6;
        if (i < 0) i += 6;
        double f = hh - Math.Floor(hh);
        double p = v * (1 - s);
        double q = v * (1 - s * f);
        double t = v * (1 - s * (1 - f));

        double r, g, b;
        switch (i)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return new Rgb(
            (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(b * 255), 0, 255));
    }
}

/// <summary>Which hue family a province's color should be drawn from - kept
/// separate so sea/lake provinces never end up looking like a shade of the
/// same color family as neighboring land/wasteland provinces (and vice
/// versa), on top of the per-id spread HsvWalk already provides. See
/// ColorAllocator.Next(ColorPalette).</summary>
public enum ColorPalette { Land, Sea }

/// <summary>
/// Hands out unique RGB colors for provinces, walking hue with a golden-
/// angle step (instead of a plain nested hue/saturation/value loop) so that
/// COLORS ALLOCATED BACK TO BACK land far apart on the color wheel rather
/// than only differing in shade - important because province ids are handed
/// out in roughly seed-placement/scan order, so ids next to each other are
/// very often spatially adjacent provinces on the map, and "next to each
/// other" is exactly where two similar-looking shades of the same hue are
/// hardest to tell apart. Land and sea/lake provinces additionally draw
/// from two disjoint hue bands (see ColorPalette) so the two domains never
/// read as the same color family either. We never run out of distinct
/// colors and never accidentally reuse one.
/// </summary>
public sealed class ColorAllocator
{
    private readonly HashSet<Rgb> _used = new();
    private readonly IEnumerator<Rgb> _landGen;
    private readonly IEnumerator<Rgb> _seaGen;

    public ColorAllocator(IEnumerable<Rgb>? reserved = null)
    {
        if (reserved != null)
        {
            foreach (var c in reserved) _used.Add(c);
        }
        _landGen = HsvWalk(ColorPalette.Land).GetEnumerator();
        _seaGen = HsvWalk(ColorPalette.Sea).GetEnumerator();
    }

    public void Reserve(Rgb color) => _used.Add(color);

    public Rgb Next(ColorPalette palette = ColorPalette.Land)
    {
        var gen = palette == ColorPalette.Sea ? _seaGen : _landGen;
        while (gen.MoveNext())
        {
            var candidate = gen.Current;
            if (_used.Add(candidate)) return candidate;
        }
        throw new InvalidOperationException("color generator exhausted (should not happen)");
    }

    public int Count => _used.Count;

    private static IEnumerable<Rgb> HsvWalk(ColorPalette palette)
    {
        const double golden = 0.6180339887498949; // 1/phi - the standard "maximally spread sequential picks" step
        // Land keeps the wide majority of the wheel (warm through green
        // through violet); sea/lake are confined to a cyan..blue band, so
        // the two domains are never confusable by hue alone, on top of
        // each domain's own ids being spread far apart within its band.
        double bandStart, bandWidth;
        if (palette == ColorPalette.Sea) { bandStart = 0.50; bandWidth = 0.20; }
        else { bandStart = 0.72; bandWidth = 0.80; }

        int[] satSteps = { 88, 62, 100, 50, 75 };
        int[] valSteps = { 92, 68, 100, 80 };

        for (long i = 0; ; i++)
        {
            double frac = (i * golden) % 1.0;
            double hue = (bandStart + frac * bandWidth) % 1.0;
            double sat = satSteps[i % satSteps.Length] / 100.0;
            double val = valSteps[(i / satSteps.Length) % valSteps.Length] / 100.0;
            yield return ColorMath.HsvToRgb(hue, sat, val);
        }
    }
}

public static class ColorHex
{
    public static string ToHex(Rgb c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static Rgb FromHex(string s)
    {
        s = s.TrimStart('#');
        byte r = Convert.ToByte(s.Substring(0, 2), 16);
        byte g = Convert.ToByte(s.Substring(2, 2), 16);
        byte b = Convert.ToByte(s.Substring(4, 2), 16);
        return new Rgb(r, g, b);
    }
}
