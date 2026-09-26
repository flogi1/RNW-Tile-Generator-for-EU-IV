using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RnwTileGenerator.App;

/// <summary>
/// Tiny persisted store for the 4 "save your own size" brush presets each
/// BrushSizePanel offers. Presets are kept separately per context (keyed
/// by e.g. "coastline", "mountains", "provinces-pen") since a size that
/// makes sense for a 150px land brush is meaningless clamped into a 15px
/// border pen. Stored as a small JSON file next to the .exe so presets
/// survive between runs - best-effort: any I/O or parse failure just
/// falls back to the caller's defaults rather than breaking the app.
/// </summary>
public static class BrushPresets
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "brush_presets.json");
    private static Dictionary<string, double[]>? _cache;

    private static Dictionary<string, double[]> LoadAll()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json);
                if (data != null)
                {
                    _cache = data;
                    return _cache;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable file - start fresh rather than crash.
        }
        _cache = new Dictionary<string, double[]>();
        return _cache;
    }

    private static void SaveAll()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache ?? new Dictionary<string, double[]>(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best effort - a preset that fails to save just resets on next launch.
        }
    }

    /// <summary>Returns the 4 stored sizes for `key`, or `defaults` if none
    /// are stored yet (or the stored count doesn't match).</summary>
    public static double[] Get(string key, double[] defaults)
    {
        var all = LoadAll();
        if (all.TryGetValue(key, out var stored) && stored.Length == defaults.Length)
            return (double[])stored.Clone();
        return (double[])defaults.Clone();
    }

    public static void SetSlot(string key, int index, double value, double[] defaults)
    {
        var all = LoadAll();
        if (!all.TryGetValue(key, out var arr) || arr.Length != defaults.Length)
            arr = (double[])defaults.Clone();
        arr[index] = value;
        all[key] = arr;
        SaveAll();
    }
}
