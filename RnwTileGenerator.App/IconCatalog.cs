using System;
using System.Collections.Generic;
using System.IO;

namespace RnwTileGenerator.App;

/// <summary>
/// Looks up and caches the real game icons (Runde 9 feedback item 7) used
/// by StageSpecialFeatures, by a short semantic key rather than a file path
/// - "estuary", "paradise", "cotCoastal", "cotInland", "strait". Icons live
/// as loose .dds files in an "Icons" folder next to the built .exe (see
/// RnwTileGenerator.App.csproj's Content item), named exactly after their
/// key (key + ".dds"). A missing file, or one DdsIcon.Load can't decode
/// (e.g. a DXT-compressed file it doesn't support), is treated the same
/// way: TryGet returns null and the caller falls back to the original
/// colored-marker drawing - so a distributed copy of the app missing (or
/// receiving a corrupt) icon file degrades gracefully rather than
/// crashing.
/// </summary>
public static class IconCatalog
{
    private static readonly Dictionary<string, DecodedIcon?> Cache = new();

    private static string IconsDir => Path.Combine(AppContext.BaseDirectory, "Icons");

    public static DecodedIcon? TryGet(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        DecodedIcon? result = null;
        try
        {
            var path = Path.Combine(IconsDir, key + ".dds");
            if (File.Exists(path)) result = DdsIcon.Load(path).FirstSquareFrameIfStrip();
        }
        catch
        {
            result = null; // missing/unreadable/unsupported - caller falls back to a colored marker
        }
        Cache[key] = result;
        return result;
    }
}
