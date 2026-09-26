using System;
using System.Collections.Generic;
using System.IO;

namespace RnwTileGenerator.Core;

/// <summary>
/// Minimal, dependency-free BMP reader/writer for exactly the three
/// variants a tile needs: 8bpp grayscale (identity palette) for the height
/// map, 8bpp indexed with a fixed custom palette for the river map, and
/// 24bpp RGB for the province map. Writing our own (instead of pulling in
/// System.Drawing or another imaging library) keeps this program's only
/// dependency on native/unsigned code at zero beyond the .NET runtime
/// itself - the byte layout was validated against both a hand-built
/// reference implementation and the two real shipped example tiles
/// (including one with a non-standard 108-byte BITMAPV4HEADER) before this
/// was written, by prototyping the exact same algorithm in Python and
/// diffing pixel-for-pixel against Pillow's own BMP parser.
///
/// Only uncompressed (BI_RGB) bitmaps are supported for reading, which is
/// all any valid tile file should ever be.
/// </summary>
public static class BmpIO
{
    public sealed class DecodedBmp
    {
        public int Width;
        public int Height;
        public int BitsPerPixel;
        public byte[]? Indices;      // for 8bpp: one byte per pixel, row-major, top-down
        public Rgb[]? Palette;       // for 8bpp: the color table (256 entries)
        public Rgb[]? Pixels;        // for 24/32bpp: one Rgb per pixel, row-major, top-down
    }

    // -- writing ----------------------------------------------------------

    public static void SaveGray8(string path, int width, int height, byte[] rowMajorTopDown)
    {
        var palette = new Rgb[256];
        for (int i = 0; i < 256; i++) palette[i] = new Rgb((byte)i, (byte)i, (byte)i);
        SaveIndexed8(path, width, height, rowMajorTopDown, palette);
    }

    public static void SaveIndexed8(string path, int width, int height, byte[] rowMajorTopDown, Rgb[] palette256)
    {
        if (rowMajorTopDown.Length != width * height)
            throw new ArgumentException("pixel buffer size does not match width*height");
        int rowBytes = width;
        int paddedRow = (rowBytes + 3) / 4 * 4;
        int pad = paddedRow - rowBytes;
        int pixelDataSize = paddedRow * height;
        int paletteSize = 256 * 4;
        int headerSize = 14 + 40 + paletteSize;
        int fileSize = headerSize + pixelDataSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0); bw.Write((short)0);
        bw.Write(headerSize);

        // BITMAPINFOHEADER
        bw.Write(40);             // biSize
        bw.Write(width);          // biWidth
        bw.Write(height);         // biHeight (positive = bottom-up)
        bw.Write((short)1);       // biPlanes
        bw.Write((short)8);       // biBitCount
        bw.Write(0);              // biCompression = BI_RGB
        bw.Write(pixelDataSize);  // biSizeImage
        bw.Write(0);              // biXPelsPerMeter
        bw.Write(0);              // biYPelsPerMeter
        bw.Write(256);            // biClrUsed
        bw.Write(0);              // biClrImportant

        for (int i = 0; i < 256; i++)
        {
            var c = i < palette256.Length ? palette256[i] : new Rgb(0, 0, 0);
            bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write((byte)0);
        }

        var padBytes = new byte[pad];
        for (int y = height - 1; y >= 0; y--)
        {
            bw.Write(rowMajorTopDown, y * width, width);
            if (pad > 0) bw.Write(padBytes);
        }
    }

    public static void SaveRgb24(string path, int width, int height, Rgb[] rowMajorTopDown)
    {
        if (rowMajorTopDown.Length != width * height)
            throw new ArgumentException("pixel buffer size does not match width*height");
        int rowBytes = width * 3;
        int paddedRow = (rowBytes + 3) / 4 * 4;
        int pad = paddedRow - rowBytes;
        int pixelDataSize = paddedRow * height;
        int headerSize = 14 + 40;
        int fileSize = headerSize + pixelDataSize;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write((byte)'B'); bw.Write((byte)'M');
        bw.Write(fileSize);
        bw.Write((short)0); bw.Write((short)0);
        bw.Write(headerSize);

        bw.Write(40);
        bw.Write(width);
        bw.Write(height);
        bw.Write((short)1);
        bw.Write((short)24);
        bw.Write(0);
        bw.Write(pixelDataSize);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);

        var padBytes = new byte[pad];
        var rowBuf = new byte[rowBytes];
        for (int y = height - 1; y >= 0; y--)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                var c = rowMajorTopDown[rowStart + x];
                rowBuf[x * 3 + 0] = c.B;
                rowBuf[x * 3 + 1] = c.G;
                rowBuf[x * 3 + 2] = c.R;
            }
            bw.Write(rowBuf);
            if (pad > 0) bw.Write(padBytes);
        }
    }

    // -- reading ------------------------------------------------------------

    public static DecodedBmp Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
            throw new InvalidDataException($"{path} is not a BMP file");

        int pixelOffset = BitConverter.ToInt32(data, 10);
        int headerSize = BitConverter.ToInt32(data, 14);
        int width = BitConverter.ToInt32(data, 18);
        int heightRaw = BitConverter.ToInt32(data, 22);
        short bpp = BitConverter.ToInt16(data, 28);
        int compression = BitConverter.ToInt32(data, 30);
        int clrUsed = headerSize >= 36 ? BitConverter.ToInt32(data, 46) : 0;

        if (compression != 0)
            throw new NotSupportedException($"{path}: compressed BMPs are not supported (compression={compression})");

        bool topDown = heightRaw < 0;
        int height = Math.Abs(heightRaw);

        Rgb[]? palette = null;
        if (bpp <= 8)
        {
            int nColors = clrUsed != 0 ? clrUsed : (1 << bpp);
            int palOffset = 14 + headerSize;
            palette = new Rgb[256];
            for (int i = 0; i < 256; i++)
            {
                if (i < nColors)
                {
                    int o = palOffset + i * 4;
                    byte b = data[o], g = data[o + 1], r = data[o + 2];
                    palette[i] = new Rgb(r, g, b);
                }
                else
                {
                    palette[i] = new Rgb(0, 0, 0);
                }
            }
        }

        int rowBytesUnpadded = (width * bpp + 7) / 8;
        int paddedRow = (rowBytesUnpadded + 3) / 4 * 4;

        var result = new DecodedBmp { Width = width, Height = height, BitsPerPixel = bpp };

        if (bpp == 8)
        {
            var indices = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                int srcRow = topDown ? row : (height - 1 - row);
                int rowStart = pixelOffset + srcRow * paddedRow;
                Array.Copy(data, rowStart, indices, row * width, width);
            }
            result.Indices = indices;
            result.Palette = palette;
        }
        else if (bpp == 24 || bpp == 32)
        {
            int bytesPerPixel = bpp / 8;
            var pixels = new Rgb[width * height];
            for (int row = 0; row < height; row++)
            {
                int srcRow = topDown ? row : (height - 1 - row);
                int rowStart = pixelOffset + srcRow * paddedRow;
                for (int x = 0; x < width; x++)
                {
                    int o = rowStart + x * bytesPerPixel;
                    byte b = data[o], g = data[o + 1], r = data[o + 2];
                    pixels[row * width + x] = new Rgb(r, g, b);
                }
            }
            result.Pixels = pixels;
        }
        else
        {
            throw new NotSupportedException($"{path}: unsupported bit depth {bpp}");
        }

        return result;
    }
}

/// <summary>
/// Higher-level tile-file-shaped wrappers over BmpIO: the height map (8bpp
/// grayscale), the river map (8bpp indexed with the game's fixed river
/// palette), and the province map (24bpp RGB). Mirrors the original Python
/// "rnw/bmpio.py" module's save_*/load_* helpers.
/// </summary>
public static class TileBitmaps
{
    // -- height map ---------------------------------------------------------

    public static void SaveHeightBmp(string path, GrayMap height) =>
        BmpIO.SaveGray8(path, height.Width, height.Height, height.Data);

    public static GrayMap LoadHeightBmp(string path)
    {
        var d = BmpIO.Load(path);
        var map = new GrayMap(d.Width, d.Height);
        if (d.Indices != null)
        {
            Array.Copy(d.Indices, map.Data, d.Indices.Length);
        }
        else if (d.Pixels != null)
        {
            // Rare fallback: a height map that wasn't saved as 8bpp
            // grayscale. Convert via the standard luma formula, matching
            // Pillow's convert("L").
            for (int i = 0; i < d.Pixels.Length; i++)
            {
                var p = d.Pixels[i];
                map.Data[i] = (byte)Math.Clamp(Math.Round(p.R * 0.299 + p.G * 0.587 + p.B * 0.114), 0, 255);
            }
        }
        return map;
    }

    // -- river map ------------------------------------------------------------

    /// <summary>256-entry palette matching the shipped examples.</summary>
    public static Rgb[] BuildRiverPalette()
    {
        var pal = new Rgb[256];
        pal[Constants.RiverPalSource] = Constants.RiverColorSource;
        pal[Constants.RiverPalMerge] = Constants.RiverColorMerge;
        pal[Constants.RiverPalSplit] = Constants.RiverColorSplit;
        for (int i = 0; i < Constants.RiverColorsBlue.Length; i++)
            pal[Constants.RiverPalBlueStart + i] = Constants.RiverColorsBlue[i];
        pal[Constants.RiverPalSeaBg] = Constants.RiverColorSeaBg;
        pal[Constants.RiverPalLandBg] = Constants.RiverColorLandBg;
        return pal;
    }

    /// <summary>indexArray: row-major top-down palette indices.</summary>
    public static void SaveRiverBmp(string path, byte[] indexArray, int width, int height) =>
        BmpIO.SaveIndexed8(path, width, height, indexArray, BuildRiverPalette());

    public static byte[] LoadRiverBmp(string path)
    {
        var d = BmpIO.Load(path);
        if (d.Indices != null) return d.Indices;

        if (d.Pixels != null)
        {
            // Rare fallback for a river map saved as RGB instead of
            // indexed: snap every pixel to whichever of the format's known
            // semantic colors is nearest.
            var palette = BuildRiverPalette();
            var meaningful = new List<(byte idx, Rgb color)>
            {
                (Constants.RiverPalSource, palette[Constants.RiverPalSource]),
                (Constants.RiverPalMerge, palette[Constants.RiverPalMerge]),
                (Constants.RiverPalSplit, palette[Constants.RiverPalSplit]),
                (Constants.RiverPalSeaBg, palette[Constants.RiverPalSeaBg]),
                (Constants.RiverPalLandBg, palette[Constants.RiverPalLandBg]),
            };
            for (int i = 0; i < Constants.RiverColorsBlue.Length; i++)
                meaningful.Add(((byte)(Constants.RiverPalBlueStart + i), palette[Constants.RiverPalBlueStart + i]));

            var outIdx = new byte[d.Pixels.Length];
            for (int i = 0; i < d.Pixels.Length; i++)
            {
                var p = d.Pixels[i];
                byte best = meaningful[0].idx;
                int bestDist = int.MaxValue;
                foreach (var (idx, c) in meaningful)
                {
                    int dr = p.R - c.R, dg = p.G - c.G, db = p.B - c.B;
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist) { bestDist = dist; best = idx; }
                }
                outIdx[i] = best;
            }
            return outIdx;
        }
        throw new NotSupportedException($"{path}: could not read a river map (no indexed or RGB pixel data)");
    }

    // -- province map ---------------------------------------------------------

    public static void SaveProvinceBmp(string path, RgbMap rgb) =>
        BmpIO.SaveRgb24(path, rgb.Width, rgb.Height, rgb.Data);

    public static RgbMap LoadProvinceBmp(string path)
    {
        var d = BmpIO.Load(path);
        var map = new RgbMap(d.Width, d.Height);
        if (d.Pixels != null)
        {
            Array.Copy(d.Pixels, map.Data, d.Pixels.Length);
        }
        else if (d.Indices != null && d.Palette != null)
        {
            for (int i = 0; i < d.Indices.Length; i++)
                map.Data[i] = d.Palette[d.Indices[i]];
        }
        return map;
    }
}
