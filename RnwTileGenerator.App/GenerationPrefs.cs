using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RnwTileGenerator.App;

/// <summary>
/// Tiny persisted store for "remember the values I used last time" slider
/// (and a few checkbox) settings, same pattern/location as
/// BrushPresets.cs and Localization.cs - a small JSON file next to the
/// .exe, project-independent (unlike GenerationSettings, which lives
/// inside one .rnwproj/tile and is about that tile specifically). Used
/// mainly by the Random Generation tab, whose whole point is one-click
/// "generate a tile with these settings" - so the settings a person
/// actually used last are exactly what they're likely to want as the
/// starting point next time, across different tiles and app restarts.
/// Best-effort: any I/O or parse failure just falls back to the caller's
/// defaults rather than breaking the app.
/// </summary>
public static class GenerationPrefs
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "generation_prefs.json");
    private static Dictionary<string, double>? _cache;

    private static Dictionary<string, double> LoadAll()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, double>>(json);
                if (data != null) { _cache = data; return _cache; }
            }
        }
        catch
        {
            // Corrupt or unreadable file - start fresh rather than crash.
        }
        _cache = new Dictionary<string, double>();
        return _cache;
    }

    private static void SaveAll()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache ?? new Dictionary<string, double>(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best effort - a value that fails to save just resets on next launch.
        }
    }

    public static double GetDouble(string key, double fallback)
    {
        var all = LoadAll();
        return all.TryGetValue(key, out var v) ? v : fallback;
    }

    public static bool GetBool(string key, bool fallback) => GetDouble(key, fallback ? 1 : 0) >= 0.5;

    public static void Set(string key, double value)
    {
        var all = LoadAll();
        all[key] = value;
        SaveAll();
    }

    public static void SetBool(string key, bool value) => Set(key, value ? 1 : 0);
}
