using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RnwTileGenerator.App;

/// <summary>One saved random-generation seed, with just enough context
/// (tile size, water%, a free-text note) to make an old entry meaningful
/// again later without having to remember what it was for.</summary>
public sealed class SavedSeed
{
    public int Seed { get; set; }
    public string Note { get; set; } = "";
    public int GridW { get; set; }
    public int GridH { get; set; }
    public double WaterPercent { get; set; }
    [JsonIgnore]
    public DateTime SavedAtUtc { get; set; }
    public string SavedAt { get; set; } = "";

    public override string ToString()
    {
        string label = string.IsNullOrWhiteSpace(Note) ? "(no note)" : Note;
        return $"{Seed}  -  {GridW}x{GridH}, {WaterPercent:0}% water  -  {label}";
    }
}

/// <summary>
/// A tiny persisted "favorite seeds" list, same JSON-next-to-the-exe
/// pattern as BrushPresets.cs/GenerationPrefs.cs, project-independent (a
/// good seed for an archipelago is worth reusing on a totally different
/// tile later, not tied to any one .rnwproj). Deliberately simple - a flat
/// list, no tagging/search beyond the free-text note - rather than a real
/// database, in keeping with the project's zero-dependency, single-file
/// philosophy.
/// </summary>
public static class SeedStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "saved_seeds.json");
    private static List<SavedSeed>? _cache;

    private static List<SavedSeed> LoadAll()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<List<SavedSeed>>(json);
                if (data != null) { _cache = data; return _cache; }
            }
        }
        catch
        {
            // Corrupt or unreadable file - start fresh rather than crash.
        }
        _cache = new List<SavedSeed>();
        return _cache;
    }

    public static IReadOnlyList<SavedSeed> All() => LoadAll();

    public static void Add(SavedSeed seed)
    {
        seed.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var all = LoadAll();
        all.Insert(0, seed); // newest first
        Save();
    }

    public static void RemoveAt(int index)
    {
        var all = LoadAll();
        if (index < 0 || index >= all.Count) return;
        all.RemoveAt(index);
        Save();
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache ?? new List<SavedSeed>(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best effort - a seed that fails to save just isn't there next launch.
        }
    }
}
