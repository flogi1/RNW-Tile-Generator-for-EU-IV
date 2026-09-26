using System;
using System.Windows;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>
/// Turning project state into RGB24 crops for PaintCanvasControl. Direct
/// port of the original Python "rnw/render.py" module. Every Composite*
/// function takes a project and a rect in full-image pixel coordinates and
/// returns a tightly-packed RGB24 buffer sized exactly rect.Width *
/// rect.Height * 3 bytes - i.e. only the requested crop is ever processed,
/// which is what keeps the GUI responsive while zoomed into a large tile.
/// </summary>
public static class Render
{
    public static readonly Rgb ColorSea = new(40, 90, 160);
    public static readonly Rgb ColorLand = new(210, 195, 150);
    public static readonly Rgb ColorWasteland = new(150, 120, 90);
    public static readonly Rgb ColorEmpty = new(90, 90, 90);
    public static readonly Rgb ColorMountainTint = new(120, 60, 30);

    private static byte[] BaseRgb(TileProject p, int x0, int y0, int w, int h)
    {
        var outBuf = new byte[w * h * 3];
        int fullW = p.LandMask.Width;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (y0 + y) * fullW;
            for (int x = 0; x < w; x++)
            {
                int srcIdx = srcRow + x0 + x;
                bool land = p.LandMask.Data[srcIdx] >= 128;
                Rgb c = land ? ColorLand : ColorSea;
                if (land && p.WastelandMask.Data[srcIdx] >= 128) c = ColorWasteland;
                if (p.EmptyMask.Data[srcIdx] >= 128) c = ColorEmpty;
                int o = (y * w + x) * 3;
                outBuf[o] = c.R; outBuf[o + 1] = c.G; outBuf[o + 2] = c.B;
            }
        }
        return outBuf;
    }

    /// <summary>Draws the 128px grid. `gray` is the line brightness, `alpha`
    /// (0-1) blends it over what is underneath - below 1 the pixels beneath
    /// a grid line stay recognisable (used where thin details would
    /// otherwise vanish under an opaque line).</summary>
    private static void DrawGrid(byte[] buf, int x0, int y0, int w, int h, byte gray = 255, double alpha = 1.0)
    {
        void Put(int o)
        {
            if (alpha >= 1.0) { buf[o] = gray; buf[o + 1] = gray; buf[o + 2] = gray; return; }
            for (int k = 0; k < 3; k++) buf[o + k] = (byte)(buf[o + k] * (1 - alpha) + gray * alpha + 0.5);
        }
        int firstCol = ((x0 + Constants.GridUnit - 1) / Constants.GridUnit) * Constants.GridUnit;
        for (int gx = firstCol; gx < x0 + w; gx += Constants.GridUnit)
        {
            int lx = gx - x0;
            if (lx < 0 || lx >= w) continue;
            for (int y = 0; y < h; y++)
            {
                Put((y * w + lx) * 3);
            }
        }
        int firstRow = ((y0 + Constants.GridUnit - 1) / Constants.GridUnit) * Constants.GridUnit;
        for (int gy = firstRow; gy < y0 + h; gy += Constants.GridUnit)
        {
            int ly = gy - y0;
            if (ly < 0 || ly >= h) continue;
            int rowStart = ly * w * 3;
            for (int x = 0; x < w; x++)
            {
                Put(rowStart + x * 3);
            }
        }
    }

    public static byte[] CompositeCoastline(TileProject p, Int32Rect rect, bool showGrid)
    {
        var buf = BaseRgb(p, rect.X, rect.Y, rect.Width, rect.Height);
        if (showGrid) DrawGrid(buf, rect.X, rect.Y, rect.Width, rect.Height);
        return buf;
    }

    public static byte[] CompositeMountains(TileProject p, Int32Rect rect, bool showGrid)
    {
        int w = rect.Width, h = rect.Height;
        var buf = BaseRgb(p, rect.X, rect.Y, w, h);
        int fullW = p.MountainMask.Width;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (rect.Y + y) * fullW;
            for (int x = 0; x < w; x++)
            {
                double mtn = Math.Clamp(p.MountainMask.Data[srcRow + rect.X + x] / 255.0, 0.0, 1.0);
                int o = (y * w + x) * 3;
                buf[o] = (byte)Math.Clamp(buf[o] * (1 - 0.85 * mtn) + ColorMountainTint.R * (0.85 * mtn), 0, 255);
                buf[o + 1] = (byte)Math.Clamp(buf[o + 1] * (1 - 0.85 * mtn) + ColorMountainTint.G * (0.85 * mtn), 0, 255);
                buf[o + 2] = (byte)Math.Clamp(buf[o + 2] * (1 - 0.85 * mtn) + ColorMountainTint.B * (0.85 * mtn), 0, 255);
            }
        }
        if (showGrid) DrawGrid(buf, rect.X, rect.Y, w, h);
        return buf;
    }

    public static byte[] CompositeHeight(TileProject p, Int32Rect rect)
    {
        int w = rect.Width, h = rect.Height;
        if (p.Height == null) return BaseRgb(p, rect.X, rect.Y, w, h);
        var buf = new byte[w * h * 3];
        int fullW = p.Height.Width;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (rect.Y + y) * fullW;
            for (int x = 0; x < w; x++)
            {
                byte v = p.Height.Data[srcRow + rect.X + x];
                int o = (y * w + x) * 3;
                buf[o] = v; buf[o + 1] = v; buf[o + 2] = v;
            }
        }
        return buf;
    }

    public static byte[] CompositeRivers(TileProject p, Int32Rect rect, bool showGrid, int? highlightSegment = null)
    {
        int w = rect.Width, h = rect.Height;
        var buf = new byte[w * h * 3];
        int fullW = p.LandMask.Width;
        var riverIdx = p.RiverRaster();

        // Pass 1: land/sea background. Pass 2 (after the grid): river
        // pixels - the white grid lines must sit UNDER the river, otherwise
        // they overpaint river pixels that cross a 128px line and show up
        // as single-pixel gaps in the on-screen river (display only, the
        // exported bitmap never has the grid).
        for (int y = 0; y < h; y++)
        {
            int srcRow = (rect.Y + y) * fullW;
            for (int x = 0; x < w; x++)
            {
                Rgb c = p.LandMask.Data[srcRow + rect.X + x] >= 128 ? Constants.RiverColorLandBg : Constants.RiverColorSeaBg;
                int o = (y * w + x) * 3;
                buf[o] = c.R; buf[o + 1] = c.G; buf[o + 2] = c.B;
            }
        }
        if (showGrid) DrawGrid(buf, rect.X, rect.Y, w, h, gray: 0);
        for (int y = 0; y < h; y++)
        {
            int srcRow = (rect.Y + y) * fullW;
            for (int x = 0; x < w; x++)
            {
                byte pal = riverIdx[srcRow + rect.X + x];
                Rgb c;
                if (pal == Constants.RiverPalSource) c = Constants.RiverColorSource;
                else if (pal == Constants.RiverPalMerge) c = Constants.RiverColorMerge;
                else if (pal == Constants.RiverPalSplit) c = Constants.RiverColorSplit;
                else if (pal >= Constants.RiverPalBlueStart && pal < Constants.RiverPalBlueStart + Constants.RiverColorsBlue.Length)
                    c = Constants.RiverColorsBlue[pal - Constants.RiverPalBlueStart];
                else continue;
                int o = (y * w + x) * 3;
                buf[o] = c.R; buf[o + 1] = c.G; buf[o + 2] = c.B;
            }
        }

        // Selected segment in the Rivers tab list: repainted red on top (display only).
        if (highlightSegment.HasValue && highlightSegment.Value >= 0 && highlightSegment.Value < p.RiverSegments.Count)
        {
            foreach (var (px, py) in RiverMapGen.RasterizeSegment(p.RiverSegments[highlightSegment.Value]))
            {
                int lx = px - rect.X, ly = py - rect.Y;
                if (lx < 0 || lx >= w || ly < 0 || ly >= h) continue;
                int o = (ly * w + lx) * 3;
                buf[o] = 255; buf[o + 1] = 0; buf[o + 2] = 0;
            }
        }
        return buf;
    }

    public static byte[] CompositeProvinces(TileProject p, Int32Rect rect, bool showGrid, int? highlightId)
    {
        int w = rect.Width, h = rect.Height;
        if (p.ProvinceResult == null)
        {
            var empty = BaseRgb(p, rect.X, rect.Y, w, h);
            if (showGrid) DrawGrid(empty, rect.X, rect.Y, w, h);
            return empty;
        }
        var buf = new byte[w * h * 3];
        int fullW = p.ProvinceResult.Rgb.Width;
        for (int y = 0; y < h; y++)
        {
            int srcRow = (rect.Y + y) * fullW;
            for (int x = 0; x < w; x++)
            {
                int srcIdx = srcRow + rect.X + x;
                Rgb c = p.ProvinceResult.Rgb.Data[srcIdx];
                if (highlightId.HasValue && p.ProvinceResult.Labels.Data[srcIdx] == highlightId.Value)
                {
                    c = new Rgb(
                        (byte)Math.Clamp(c.R + 80, 0, 255),
                        (byte)Math.Clamp(c.G + 80, 0, 255),
                        (byte)Math.Clamp(c.B + 80, 0, 255));
                }
                int o = (y * w + x) * 3;
                buf[o] = c.R; buf[o + 1] = c.G; buf[o + 2] = c.B;
            }
        }
        if (showGrid) DrawGrid(buf, rect.X, rect.Y, w, h, alpha: 0.45);
        return buf;
    }
}
