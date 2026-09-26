using System;

namespace RnwTileGenerator.Core;

/// <summary>
/// A single-channel byte raster. Used both for boolean masks (only values
/// 0 / 255 ever appear) and for continuous data (height, mountain
/// intensity), which is why this is one type rather than two - it keeps
/// the brush/flood-fill/blur code shared between both uses, exactly like
/// the original Python prototype's "PIL 'L' image" convention.
/// </summary>
public sealed class GrayMap
{
    public readonly int Width;
    public readonly int Height;
    public readonly byte[] Data;

    public GrayMap(int width, int height, byte fill = 0)
    {
        Width = width;
        Height = height;
        Data = new byte[width * height];
        if (fill != 0) Array.Fill(Data, fill);
    }

    public byte this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public bool GetBool(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height) return false;
        return Data[y * Width + x] >= 128;
    }

    public void SetBool(int x, int y, bool value) => this[x, y] = value ? (byte)255 : (byte)0;

    public bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    public GrayMap Clone()
    {
        var m = new GrayMap(Width, Height);
        Array.Copy(Data, m.Data, Data.Length);
        return m;
    }

    public static GrayMap Bool(int width, int height, bool value = false) => new(width, height, value ? (byte)255 : (byte)0);

    public int CountTrue()
    {
        int n = 0;
        for (int i = 0; i < Data.Length; i++) if (Data[i] >= 128) n++;
        return n;
    }

    public bool AnyTrue() => CountTrue() > 0;
    public bool AllTrue() => CountTrue() == Data.Length;

    // -- boolean algebra (values are assumed to be 0/255 already) ----------

    public static GrayMap And(GrayMap a, GrayMap b)
    {
        var r = new GrayMap(a.Width, a.Height);
        for (int i = 0; i < r.Data.Length; i++) r.Data[i] = Math.Min(a.Data[i], b.Data[i]);
        return r;
    }

    public static GrayMap Or(GrayMap a, GrayMap b)
    {
        var r = new GrayMap(a.Width, a.Height);
        for (int i = 0; i < r.Data.Length; i++) r.Data[i] = Math.Max(a.Data[i], b.Data[i]);
        return r;
    }

    public static GrayMap Not(GrayMap a)
    {
        var r = new GrayMap(a.Width, a.Height);
        for (int i = 0; i < r.Data.Length; i++) r.Data[i] = (byte)(255 - a.Data[i]);
        return r;
    }
}

/// <summary>32-bit signed labels (province ids). -1 means "unassigned".</summary>
public sealed class LabelMap
{
    public readonly int Width;
    public readonly int Height;
    public readonly int[] Data;

    public LabelMap(int width, int height, int fill = -1)
    {
        Width = width;
        Height = height;
        Data = new int[width * height];
        if (fill != 0) Array.Fill(Data, fill);
    }

    public int this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }
}

/// <summary>24-bit RGB raster (the province map).</summary>
public sealed class RgbMap
{
    public readonly int Width;
    public readonly int Height;
    public readonly Rgb[] Data;

    public RgbMap(int width, int height, Rgb fill = default)
    {
        Width = width;
        Height = height;
        Data = new Rgb[width * height];
        if (!fill.Equals(default(Rgb))) Array.Fill(Data, fill);
    }

    public Rgb this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }
}
