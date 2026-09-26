using System;

namespace RnwTileGenerator.App;

/// <summary>
/// "Reference image overlay" (Runde 9 feedback item 3): a second image the
/// user can load as a translucent visual aid on top of any stage's canvas,
/// to help hand-trace an authentic real-world map ("als zweite Ebene...
/// als Referenzbild"). Deliberately app-level and NOT part of TileProject -
/// it is a pure viewing aid, never written into a .rnwproj save/undo
/// snapshot (TileProject.SaveTo/LoadFrom only ever writes named entries it
/// explicitly lists, so a field never referenced there is simply never
/// persisted - see TileProject.cs) and never exported into the actual RNW
/// tile files either. It lives for the current TileProject instance only:
/// MainWindow clears it whenever a different tile is opened/created so a
/// stale overlay from a previous tile can never silently linger over an
/// unrelated one.
///
/// PaintCanvasControl blends this in for every stage automatically (see
/// its Redraw()), so it works the same on Coastline, Mountains, Rivers and
/// Provinces alike rather than needing separate wiring per stage.
/// </summary>
public static class ReferenceOverlay
{
    /// <summary>Tightly-packed RGB24 buffer, resized at load time to
    /// exactly match the current tile's WidthPx x HeightPx so it can be
    /// cropped with the exact same rect the base render uses - or null if
    /// nothing is loaded.</summary>
    public static byte[]? Rgb { get; private set; }
    public static int Width { get; private set; }
    public static int Height { get; private set; }

    /// <summary>Whether the overlay should currently be drawn (independent
    /// of whether one is loaded, so toggling it off/on doesn't forget the
    /// loaded image).</summary>
    public static bool Enabled { get; set; }

    /// <summary>0 (invisible) .. 1 (fully opaque, hides the base render
    /// entirely) - 0.5 by default, a middle ground that keeps both the
    /// generated tile and the reference map legible at once.</summary>
    public static double Opacity { get; set; } = 0.5;

    /// <summary>Loads and stores the overlay. Callers (MainWindow's
    /// toolbar) are responsible for requesting a redraw of whichever
    /// canvas is currently visible afterwards - every stage's
    /// PaintCanvasControl re-reads this state on its own next Redraw()
    /// regardless (e.g. simply by switching tabs), so no change-
    /// notification/event plumbing is needed here.</summary>
    public static void Load(string path, int targetW, int targetH)
    {
        Rgb = ImageIO.LoadRgb24Resized(path, targetW, targetH);
        Width = targetW;
        Height = targetH;
        Enabled = true;
    }

    public static void Clear()
    {
        Rgb = null;
        Width = 0;
        Height = 0;
        Enabled = false;
    }

    /// <summary>Blends the overlay (if enabled and loaded, and its stored
    /// size still matches the tile currently on screen - a stale overlay
    /// from a since-closed differently-sized tile is silently skipped
    /// rather than drawn misaligned or throwing) into `buf`, an RGB24
    /// crop starting at (x0,y0) sized w x h, in place.</summary>
    public static void BlendInto(byte[] buf, int x0, int y0, int w, int h)
    {
        if (!Enabled || Rgb == null || Width <= 0 || Height <= 0) return;
        double a = Math.Clamp(Opacity, 0.0, 1.0);
        if (a <= 0.0) return;
        for (int y = 0; y < h; y++)
        {
            int srcY = y0 + y;
            if (srcY < 0 || srcY >= Height) continue;
            int srcRow = srcY * Width * 3;
            int dstRow = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int srcX = x0 + x;
                if (srcX < 0 || srcX >= Width) continue;
                int so = srcRow + srcX * 3;
                int doo = dstRow + x * 3;
                buf[doo] = (byte)Math.Clamp(buf[doo] * (1 - a) + Rgb[so] * a, 0, 255);
                buf[doo + 1] = (byte)Math.Clamp(buf[doo + 1] * (1 - a) + Rgb[so + 1] * a, 0, 255);
                buf[doo + 2] = (byte)Math.Clamp(buf[doo + 2] * (1 - a) + Rgb[so + 2] * a, 0, 255);
            }
        }
    }
}
