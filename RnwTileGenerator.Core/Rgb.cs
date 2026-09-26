namespace RnwTileGenerator.Core;

/// <summary>An 24-bit RGB color triple. Used both for province colors and
/// for the fixed palette colors in the text file / river map.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public override string ToString() => $"({R}, {G}, {B})";
}
