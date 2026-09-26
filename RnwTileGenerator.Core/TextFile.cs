using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RnwTileGenerator.Core;

/// <summary>
/// Reading and writing the tile's .txt "script" file. Direct port of the
/// original Python "rnw/textfile.py" module.
///
/// The format is a simple Paradox-clause script: `key = value` statements,
/// where a value is either a bare token, a quoted string, or a
/// brace-enclosed block that is *either* a flat list of scalars
/// (e.g. `{ R G B }`) *or* another sequence of key = value statements
/// (e.g. `strait = { from = {...} ... }`). Comments start with `#` and run
/// to the end of the line. Keys may repeat (sea_province appears once per
/// sea province, for example) so this parses into an ordered list of
/// (key, value) pairs rather than a dictionary.
/// </summary>
public sealed class Strait
{
    public Rgb From;
    public Rgb To;
    public Rgb Through;
}

public sealed class TileMetadata
{
    public bool DoNotRotate;
    public bool DoNotRotateOrMirror;
    public bool RestrictToNorthEdge;
    public bool RestrictToSouthEdge;
    public bool RestrictToEquator;
    public bool Continent;
    public bool Fantasy;
    public int? Unique;
    public int Weight = 100;
    public List<Strait> Straits { get; set; } = new();
    /// <summary>One representative color per region.</summary>
    public List<Rgb> Regions { get; set; } = new();
    /// <summary>e.g. "river_estuary_modifier" -> [colors].</summary>
    public Dictionary<string, List<Rgb>> Modifiers { get; set; } = new();
    public Dictionary<Rgb, string> ProvinceNames { get; set; } = new();
    public Rgb EmptyColor = Constants.DefaultEmptyColor;
    /// <summary>Passthrough for unrecognized top-level fields, e.g.
    /// "add_moisture = 10000" - kept as ordered (key, rawValue) pairs so a
    /// re-saved file stays close to what a human wrote.</summary>
    public List<(string Key, string Value)> ExtraFields { get; set; } = new();
}

public sealed class ParsedTile
{
    public List<Rgb> SeaProvinces { get; set; } = new();
    public List<Rgb> LakeProvinces { get; set; } = new();
    public List<Rgb> WastelandProvinces { get; set; } = new();
    public int? NumSeaProvinces;
    public int? NumLandProvinces;
    public (int w, int h)? Size;
    public int Weight = 100;
    public TileMetadata Metadata { get; set; } = new();
}

public sealed class WriteTileOptions
{
    public required (int w, int h) SizeGrid;
    public required int NumSeaProvinces;
    public required int NumLandProvinces;
    public List<Rgb> SeaColors { get; set; } = new();
    public List<Rgb> LakeColors { get; set; } = new();
    public List<Rgb> WastelandColors { get; set; } = new();
    public required TileMetadata Metadata;
}

public static class TextFileIO
{
    // Paradox script files are plain ASCII/latin-1.
    private static readonly Encoding TextEncoding = Encoding.Latin1;

    private static readonly Regex TokenRe = new("\"[^\"]*\"|[{}=]|[^\\s{}=]+", RegexOptions.Compiled);
    private static readonly Regex IntTripleRe = new(@"^-?\d+$", RegexOptions.Compiled);

    private static readonly HashSet<string> KnownFlags = new()
    {
        "do_not_rotate", "do_not_rotate_or_mirror", "restrict_to_north_edge",
        "restrict_to_south_edge", "restrict_to_equator", "continent", "fantasy",
    };

    // -- tokenizing / generic parsing --------------------------------------

    private static string StripComments(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var outLines = normalized.Split('\n').Select(line =>
        {
            int idx = line.IndexOf('#');
            return idx < 0 ? line : line.Substring(0, idx);
        });
        return string.Join("\n", outLines);
    }

    private static List<string> Tokenize(string text) =>
        TokenRe.Matches(text).Select(m => m.Value).ToList();

    /// <summary>Parse until (but not consuming) the matching '}' (or end of
    /// tokens). Returns either a flat List&lt;string&gt; of scalar tokens, or
    /// a List&lt;(string key, object value)&gt; of pairs, matching whichever
    /// shape this block actually has - never both.</summary>
    private static (object value, int nextIndex) ParseBlock(List<string> tokens, int i)
    {
        var pairs = new List<(string key, object value)>();
        var scalars = new List<string>();
        int n = tokens.Count;
        while (i < n && tokens[i] != "}")
        {
            if (i + 1 < n && tokens[i + 1] == "=")
            {
                string key = tokens[i];
                i += 2;
                object value;
                if (i < n && tokens[i] == "{")
                {
                    var (v, ni) = ParseBlock(tokens, i + 1);
                    value = v;
                    i = ni;
                    if (i < n && tokens[i] == "}") i += 1;
                }
                else
                {
                    value = i < n ? tokens[i] : "";
                    i += 1;
                }
                pairs.Add((key, value));
            }
            else
            {
                scalars.Add(tokens[i]);
                i += 1;
            }
        }
        if (pairs.Count > 0) return (pairs, i);
        return (scalars, i);
    }

    private static Rgb ToRgb(object? value)
    {
        if (value is List<string> list && list.Count >= 3)
        {
            return new Rgb(
                (byte)int.Parse(list[0], CultureInfo.InvariantCulture),
                (byte)int.Parse(list[1], CultureInfo.InvariantCulture),
                (byte)int.Parse(list[2], CultureInfo.InvariantCulture));
        }
        throw new FormatException($"expected an RGB triple, got {StringifyValue(value)}");
    }

    private static string Unquote(string token) =>
        token.Length >= 2 && token.StartsWith("\"") && token.EndsWith("\"") ? token[1..^1] : token;

    private static string StringifyValue(object? value)
    {
        switch (value)
        {
            case null: return "";
            case string s: return s;
            case List<string> scalars: return "{ " + string.Join(" ", scalars) + " }";
            case List<(string key, object value)> pairs:
            {
                var sb = new StringBuilder("{ ");
                foreach (var (k, v) in pairs) sb.Append(k).Append(" = ").Append(StringifyValue(v)).Append(' ');
                sb.Append('}');
                return sb.ToString();
            }
            default: return value.ToString() ?? "";
        }
    }

    private static bool IsYes(object value) =>
        (value as string ?? StringifyValue(value)).Equals("yes", StringComparison.OrdinalIgnoreCase);

    // -- public: parsing an existing .txt ----------------------------------

    public static ParsedTile ParseTileText(string path)
    {
        // Latin-1 maps every byte 0..255 to a valid codepoint, so this never
        // needs a Python-style errors="replace" fallback.
        string text = File.ReadAllText(path, TextEncoding);
        var tokens = Tokenize(StripComments(text));
        var (topLevelObj, _) = ParseBlock(tokens, 0);
        var topLevel = topLevelObj as List<(string key, object value)> ?? new List<(string key, object value)>();

        var sea = new List<Rgb>();
        var lake = new List<Rgb>();
        var waste = new List<Rgb>();
        var straits = new List<Strait>();
        var regions = new List<Rgb>();
        var modifiers = new Dictionary<string, List<Rgb>>();
        var provinceNames = new Dictionary<Rgb, string>();

        bool doNotRotate = false, doNotRotateOrMirror = false;
        bool restrictNorth = false, restrictSouth = false, restrictEquator = false, continent = false, fantasy = false;
        int? uniqueVal = null;
        int weight = 100;
        int? numSea = null, numLand = null;
        (int, int)? size = null;
        Rgb emptyColor = Constants.DefaultEmptyColor;
        var extraFields = new List<(string, string)>();

        foreach (var (key, value) in topLevel)
        {
            string kl = key.ToLowerInvariant();
            if (kl == "sea_province") sea.Add(ToRgb(value));
            else if (kl == "lake_province") lake.Add(ToRgb(value));
            else if (kl == "wasteland_province") waste.Add(ToRgb(value));
            else if (kl == "strait")
            {
                var d = new Dictionary<string, object>();
                if (value is List<(string key, object value)> valuePairs)
                    foreach (var (k2, v2) in valuePairs) d[k2.ToLowerInvariant()] = v2;
                try
                {
                    straits.Add(new Strait
                    {
                        From = ToRgb(d.GetValueOrDefault("from")),
                        To = ToRgb(d.GetValueOrDefault("to")),
                        Through = ToRgb(d.GetValueOrDefault("through")),
                    });
                }
                catch { /* malformed strait block: skip it, same as the original */ }
            }
            else if (kl == "region") regions.Add(ToRgb(value));
            else if (kl == "regions") { /* count is re-derived from the region = {...} lines above */ }
            else if (kl == "province_names")
            {
                if (value is List<(string key, object value)> pnPairs)
                {
                    foreach (var (nameTok, rgbVal) in pnPairs)
                    {
                        try { provinceNames[ToRgb(rgbVal)] = Unquote(nameTok); }
                        catch { /* skip malformed entry */ }
                    }
                }
            }
            else if (KnownFlags.Contains(kl))
            {
                bool v = IsYes(value);
                switch (kl)
                {
                    case "do_not_rotate": doNotRotate = v; break;
                    case "do_not_rotate_or_mirror": doNotRotateOrMirror = v; break;
                    case "restrict_to_north_edge": restrictNorth = v; break;
                    case "restrict_to_south_edge": restrictSouth = v; break;
                    case "restrict_to_equator": restrictEquator = v; break;
                    case "continent": continent = v; break;
                    case "fantasy": fantasy = v; break;
                }
            }
            else if (kl == "unique")
            {
                if (int.TryParse(value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int u)) uniqueVal = u;
            }
            else if (kl == "weight")
            {
                if (int.TryParse(value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int wv)) weight = wv;
            }
            else if (kl == "num_sea_provinces")
            {
                if (int.TryParse(value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ns)) numSea = ns;
            }
            else if (kl == "num_land_provinces")
            {
                if (int.TryParse(value as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nl)) numLand = nl;
            }
            else if (kl == "size")
            {
                if (value is List<string> sizeList && sizeList.Count >= 2 &&
                    int.TryParse(sizeList[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sw) &&
                    int.TryParse(sizeList[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sh))
                {
                    size = (sw, sh);
                }
            }
            else if (kl == "empty")
            {
                try { emptyColor = ToRgb(value); }
                catch { /* keep default */ }
            }
            else
            {
                // Anything else that looks like "{ R G B }" is treated as a
                // generic province modifier/flavor line
                // (river_estuary_modifier, important_natural_harbor,
                // level_1_center_of_trade, ...).
                if (value is List<string> scalarList && scalarList.Count == 3 && scalarList.All(v => IntTripleRe.IsMatch(v)))
                {
                    if (!modifiers.TryGetValue(key, out var listR)) modifiers[key] = listR = new List<Rgb>();
                    listR.Add(ToRgb(value));
                }
                else
                {
                    extraFields.Add((key, value is string s ? s : StringifyValue(value)));
                }
            }
        }

        var meta = new TileMetadata
        {
            DoNotRotate = doNotRotate,
            DoNotRotateOrMirror = doNotRotateOrMirror,
            RestrictToNorthEdge = restrictNorth,
            RestrictToSouthEdge = restrictSouth,
            RestrictToEquator = restrictEquator,
            Continent = continent,
            Fantasy = fantasy,
            Unique = uniqueVal,
            Weight = weight,
            Straits = straits,
            Regions = regions,
            Modifiers = modifiers,
            ProvinceNames = provinceNames,
            EmptyColor = emptyColor,
            ExtraFields = extraFields,
        };
        return new ParsedTile
        {
            SeaProvinces = sea,
            LakeProvinces = lake,
            WastelandProvinces = waste,
            NumSeaProvinces = numSea,
            NumLandProvinces = numLand,
            Size = size,
            Weight = weight,
            Metadata = meta,
        };
    }

    // -- public: writing a new .txt ----------------------------------------

    private static string FmtRgb(Rgb c) => $"{{ {c.R} {c.G} {c.B} }}";

    public static void WriteTileText(string path, WriteTileOptions opts)
    {
        var lines = new List<string>();

        if (opts.SeaColors.Count > 0)
        {
            lines.Add("# Sea provinces");
            foreach (var c in opts.SeaColors) lines.Add($"sea_province = {FmtRgb(c)}");
            lines.Add("");
        }

        if (opts.LakeColors.Count > 0)
        {
            lines.Add("# Lake provinces");
            foreach (var c in opts.LakeColors) lines.Add($"lake_province = {FmtRgb(c)}");
            lines.Add("");
        }

        if (opts.WastelandColors.Count > 0)
        {
            lines.Add("# Wasteland provinces");
            foreach (var c in opts.WastelandColors) lines.Add($"wasteland_province = {FmtRgb(c)}");
            lines.Add("");
        }

        if (opts.Metadata.Straits.Count > 0)
        {
            lines.Add("# Straits");
            foreach (var s in opts.Metadata.Straits)
            {
                lines.Add("strait = {");
                lines.Add($"\tfrom = {FmtRgb(s.From)}");
                lines.Add($"\tto = {FmtRgb(s.To)}");
                lines.Add($"\tthrough = {FmtRgb(s.Through)}");
                lines.Add("}");
            }
            lines.Add("");
        }

        if (opts.Metadata.Modifiers.Count > 0)
        {
            lines.Add("# Province flavor / modifiers");
            foreach (var (modName, colors) in opts.Metadata.Modifiers)
                foreach (var c in colors)
                    lines.Add($"{modName} = {FmtRgb(c)}");
            lines.Add("");
        }

        if (opts.Metadata.ProvinceNames.Count > 0)
        {
            lines.Add("province_names = {");
            foreach (var (rgb, name) in opts.Metadata.ProvinceNames)
            {
                string safeName = name.Replace("\"", "'");
                lines.Add($"\t\"{safeName}\" = {FmtRgb(rgb)}");
            }
            lines.Add("}");
            lines.Add("");
        }

        var flagLines = new List<string>();
        if (opts.Metadata.DoNotRotateOrMirror) flagLines.Add("do_not_rotate_or_mirror = yes");
        else if (opts.Metadata.DoNotRotate) flagLines.Add("do_not_rotate = yes");
        if (opts.Metadata.RestrictToNorthEdge) flagLines.Add("restrict_to_north_edge = yes");
        if (opts.Metadata.RestrictToSouthEdge) flagLines.Add("restrict_to_south_edge = yes");
        if (opts.Metadata.RestrictToEquator) flagLines.Add("restrict_to_equator = yes");
        if (opts.Metadata.Continent) flagLines.Add("continent = yes");
        if (opts.Metadata.Fantasy) flagLines.Add("fantasy = yes");
        if (opts.Metadata.Unique.HasValue) flagLines.Add($"unique = {opts.Metadata.Unique.Value}");
        if (flagLines.Count > 0) { lines.AddRange(flagLines); lines.Add(""); }

        foreach (var (key, value) in opts.Metadata.ExtraFields) lines.Add($"{key} = {value}");
        if (opts.Metadata.ExtraFields.Count > 0) lines.Add("");

        if (opts.Metadata.Regions.Count > 0)
        {
            lines.Add($"Regions = {opts.Metadata.Regions.Count}");
            lines.Add("");
            foreach (var c in opts.Metadata.Regions) lines.Add($"region = {FmtRgb(c)}");
            lines.Add("");
        }

        lines.Add($"num_sea_provinces = {opts.NumSeaProvinces}");
        lines.Add($"num_land_provinces = {opts.NumLandProvinces}");
        lines.Add($"size = {{ {opts.SizeGrid.w} {opts.SizeGrid.h} }}");
        lines.Add($"weight = {opts.Metadata.Weight}");
        lines.Add($"empty = {FmtRgb(opts.Metadata.EmptyColor)}");
        lines.Add("");

        File.WriteAllText(path, string.Join("\n", lines), TextEncoding);
    }
}
