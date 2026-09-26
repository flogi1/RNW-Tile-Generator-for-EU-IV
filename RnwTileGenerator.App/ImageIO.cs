using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>
/// Tiny image-loading helper for the "import an external heightmap"
/// feature. Uses only WPF's built-in imaging types (BitmapDecoder /
/// FormatConvertedBitmap / TransformedBitmap) - these ship with the .NET
/// Windows Desktop SDK itself, not as a NuGet package, so this keeps the
/// project's zero-third-party-dependency property intact while still being
/// able to read any common format (JPEG/PNG/BMP/...) rather than only the
/// hand-rolled BMP reader RnwTileGenerator.Core.BmpIO supports for the
/// game's own tile files.
/// </summary>
public static class ImageIO
{
    /// <summary>Loads `path` as grayscale (luminance), resized (stretched,
    /// not cropped - the source is expected to be a rough sketch, not a
    /// pixel-precise photo) to exactly targetW x targetH.</summary>
    public static GrayMap LoadGrayscaleResized(string path, int targetW, int targetH)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var gray = new FormatConvertedBitmap(frame, PixelFormats.Gray8, null, 0);

        double scaleX = targetW / (double)gray.PixelWidth;
        double scaleY = targetH / (double)gray.PixelHeight;
        BitmapSource resized = gray;
        if (Math.Abs(scaleX - 1.0) > 1e-6 || Math.Abs(scaleY - 1.0) > 1e-6)
            resized = new TransformedBitmap(gray, new ScaleTransform(scaleX, scaleY));

        var map = new GrayMap(targetW, targetH);
        int stride = targetW; // Gray8 = 1 byte/pixel
        var buffer = new byte[targetW * targetH];
        resized.CopyPixels(buffer, stride, 0);
        Array.Copy(buffer, map.Data, buffer.Length);
        return map;
    }

    /// <summary>Loads `path` as a full-color RGB24 buffer, resized
    /// (stretched, not cropped) to exactly targetW x targetH - used by the
    /// reference-image overlay (Runde 9 feedback: "ein weiteres Bild als
    /// zweite Ebene... als Referenzbild laden"), which needs the source
    /// map's actual colors rather than a single grayscale channel so the
    /// overlay stays recognizable while hand-tracing an authentic map.</summary>
    public static byte[] LoadRgb24Resized(string path, int targetW, int targetH)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        var rgb = new FormatConvertedBitmap(frame, PixelFormats.Rgb24, null, 0);

        double scaleX = targetW / (double)rgb.PixelWidth;
        double scaleY = targetH / (double)rgb.PixelHeight;
        BitmapSource resized = rgb;
        if (Math.Abs(scaleX - 1.0) > 1e-6 || Math.Abs(scaleY - 1.0) > 1e-6)
            resized = new TransformedBitmap(rgb, new ScaleTransform(scaleX, scaleY));

        int stride = targetW * 3;
        var buffer = new byte[targetH * stride];
        resized.CopyPixels(buffer, stride, 0);
        return buffer;
    }

    /// <summary>Reads just the pixel dimensions without decoding/resizing -
    /// used to show the user the source image's native size before they
    /// pick a tile grid size.</summary>
    public static (int width, int height) ReadSize(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>Builds an RGB24 preview buffer (land = tan, sea = blue,
    /// mountain-intensity tinted red) at reduced resolution, purely for a
    /// quick "does this look right" preview while tuning import
    /// parameters - never the buffer actually imported.</summary>
    public static byte[] PreviewRgb(GrayMap land, GrayMap mountain)
    {
        int w = land.Width, h = land.Height;
        var buf = new byte[w * h * 3];
        for (int i = 0; i < land.Data.Length; i++)
        {
            bool isLand = land.Data[i] >= 128;
            double mtn = mountain.Data[i] / 255.0;
            byte r, g, b;
            if (isLand) { r = (byte)(210 - mtn * 90); g = (byte)(195 - mtn * 135); b = (byte)(150 - mtn * 120); }
            else { r = 40; g = 90; b = 160; }
            int o = i * 3;
            buf[o] = r; buf[o + 1] = g; buf[o + 2] = b;
        }
        return buf;
    }
}
