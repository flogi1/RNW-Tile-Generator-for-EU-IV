using System;
using System.IO;

namespace RnwTileGenerator.App;

/// <summary>A DDS icon fully decoded into a plain top-down BGRA8888 byte
/// buffer, ready to be sampled/blended directly (see StageSpecialFeatures's
/// DrawIcon) without going through any WPF imaging types - the game's icon
/// files are tiny (32x32 to 64x64px) so there's no need for anything more
/// elaborate than a flat array here.</summary>
public sealed class DecodedIcon
{
    public required int Width;
    public required int Height;
    /// <summary>Tightly packed, width*height*4 bytes, row-major top-down,
    /// (B,G,R,A) per pixel - matching the DDS files' own on-disk byte
    /// order (see DdsIcon.Load), so no channel-swap is needed here.</summary>
    public required byte[] Bgra;

    /// <summary>If this icon is wider than it is tall by an exact integer
    /// multiple (e.g. 96x32 = 3 frames of 32x32 side by side), returns a new
    /// DecodedIcon cropped to just the first (leftmost) square frame;
    /// otherwise returns this instance unchanged. Fixes the Runde 9
    /// follow-up bug report - "der Strait Indicator Icon wird 3 mal
    /// angezeigt. Einmal reicht völlig." - strait.dds turned out to be a
    /// 96x32 horizontal sprite strip (3 frames of 32x32), and DrawIcon's
    /// naive whole-buffer nearest-neighbor scale-down was squishing all 3
    /// frames into a single small marker, reading as "shown 3 times". This
    /// is applied generically (see IconCatalog.TryGet) to every icon right
    /// after loading, so it's a safety net for any future sprite-strip-
    /// shaped icon too - every currently-square icon (estuary_icon 32x32,
    /// paradise 59x63, cot_coastal_1/cot_inland_1 64x64,
    /// important_natural_harbor 32x32) has Width &lt;= Height or a
    /// non-exact ratio and is left completely unaffected.</summary>
    public DecodedIcon FirstSquareFrameIfStrip()
    {
        if (Width <= Height || Height <= 0 || Width % Height != 0) return this;
        int frame = Height;
        int srcStride = Width * 4;
        int dstStride = frame * 4;
        var cropped = new byte[dstStride * frame];
        for (int row = 0; row < frame; row++)
            Array.Copy(Bgra, row * srcStride, cropped, row * dstStride, dstStride);
        return new DecodedIcon { Width = frame, Height = frame, Bgra = cropped };
    }
}

/// <summary>
/// Minimal reader for the specific DDS subset the game's own UI icon files
/// supplied so far all use: uncompressed (empty fourCC, plain 32-bit RGBA
/// bit masks) BGRA8888, with or without a mipmap chain - only the base
/// (largest/first) mip level is ever read, which is always the first
/// Width*Height*4 bytes right after the fixed 128-byte "DDS " + DDS_HEADER
/// block. This deliberately does NOT support DXT/BC-compressed DDS files
/// (a real block-decompressor is a much larger undertaking) - Load() spots
/// that case from the header itself and throws a clear, specific exception
/// rather than silently misreading garbage pixels; IconCatalog treats that
/// (like a missing file) as "no icon available" and falls back to the
/// original colored-marker rendering.
///
/// Every icon supplied so far (paradise.dds, estuary_icon.dds,
/// cot_coastal_1.dds, cot_inland_1.dds, strait.dds,
/// important_natural_harbor.dds) was confirmed byte-for-byte to be exactly
/// this uncompressed case (see the Runde 9 investigation in
/// claude/status.md), so this covers the app's actual icon set completely
/// today.
/// </summary>
public static class DdsIcon
{
    private const int HeaderSize = 128; // 4-byte magic + 124-byte DDS_HEADER (which itself embeds the 32-byte DDS_PIXELFORMAT at offset 72)

    public static DecodedIcon Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string name = Path.GetFileName(path);
        if (bytes.Length < HeaderSize || bytes[0] != (byte)'D' || bytes[1] != (byte)'D' || bytes[2] != (byte)'S' || bytes[3] != (byte)' ')
            throw new InvalidDataException($"'{name}' is not a DDS file (missing 'DDS ' magic).");

        int width = BitConverter.ToInt32(bytes, 16);
        int height = BitConverter.ToInt32(bytes, 12);
        uint fourCC = BitConverter.ToUInt32(bytes, 84);
        uint rgbBitCount = BitConverter.ToUInt32(bytes, 88);

        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"'{name}' has an invalid DDS size ({width}x{height}).");
        if (fourCC != 0 || rgbBitCount != 32)
            throw new NotSupportedException($"'{name}' uses a compressed/unsupported DDS pixel format (fourCC=0x{fourCC:X8}, bitCount={rgbBitCount}) - only uncompressed 32-bit BGRA icons are currently supported.");

        int stride = width * 4;
        long need = HeaderSize + (long)stride * height;
        if (bytes.Length < need)
            throw new InvalidDataException($"'{name}' is truncated: expected at least {need} bytes for a {width}x{height} BGRA8888 image, got {bytes.Length}.");

        var pixels = new byte[stride * height];
        Array.Copy(bytes, HeaderSize, pixels, 0, pixels.Length);
        return new DecodedIcon { Width = width, Height = height, Bgra = pixels };
    }
}
