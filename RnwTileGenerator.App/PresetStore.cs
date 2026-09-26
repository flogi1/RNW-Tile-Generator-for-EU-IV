using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RnwTileGenerator.App;

/// <summary>One user-saved Generation Template Preset - a name plus a full
/// GenerationPreset snapshot.</summary>
public sealed class NamedPreset
{
    public string Name { get; set; } = "";
    public GenerationPreset Values { get; set; } = new();
}

/// <summary>A preset shipped with the app itself - not user-editable or
/// -deletable. NameKey is a Loc.T(...) key rather than a raw string so its
/// displayed name follows whatever UI language is currently selected,
/// same as every other label in the app.</summary>
public sealed class BuiltInPresetDef
{
    public string NameKey { get; }
    public GenerationPreset Values { get; }
    public BuiltInPresetDef(string nameKey, GenerationPreset values)
    {
        NameKey = nameKey;
        Values = values;
    }
}

/// <summary>
/// "Generation Template Presets" (Runde 8 feedback): named, saveable and
/// loadable snapshots of every Random Generation tab setting (see
/// GenerationPreset), so a favorite combination of sliders can be reused
/// across differently-sized tiles without re-tweaking each one by hand -
/// useful for anyone generating a whole batch of tiles. Same JSON-next-to-
/// the-exe persistence pattern as SeedStore.cs/GenerationPrefs.cs for the
/// user-saved half.
///
/// The built-in half (BuiltIn below) is hardcoded directly in code rather
/// than loaded from a data file, so it always ships with the .exe with
/// nothing that could go missing from a shared/distributed copy of the
/// program - the user explicitly asked for "ein paar Presets mit in der
/// Auslieferungsversion...damit die Leute eine Orientierung haben" (a
/// handful of presets in the distributed version so people have an
/// orientation). A user can never overwrite or delete a built-in preset -
/// StageRandom's save flow rejects any user preset name that collides with
/// a built-in one, and the delete flow refuses to delete a built-in entry.
/// </summary>
public static class PresetStore
{
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "generation_presets.json");
    private static List<NamedPreset>? _cache;

    private static List<NamedPreset> LoadAllInternal()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<List<NamedPreset>>(json);
                if (data != null) { _cache = data; return _cache; }
            }
        }
        catch
        {
            // Corrupt or unreadable file - start fresh rather than crash.
        }
        _cache = new List<NamedPreset>();
        return _cache;
    }

    /// <summary>A fresh copy of the user-saved presets, newest-saved last
    /// (callers that want a specific order, e.g. alphabetical, sort it
    /// themselves - StageRandom currently just lists them in save order).</summary>
    public static List<NamedPreset> LoadUser() => new(LoadAllInternal());

    public static void AddOrReplace(string name, GenerationPreset values)
    {
        var all = LoadAllInternal();
        all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        all.Add(new NamedPreset { Name = name, Values = values });
        Save();
    }

    public static void Delete(string name)
    {
        var all = LoadAllInternal();
        all.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    private static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache ?? new List<NamedPreset>(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best effort - a preset that fails to save just isn't there next launch.
        }
    }

    /// <summary>A handful of sensible starting points shipped with the app,
    /// covering the common shapes/sizes a Random New World tile takes -
    /// meant purely as orientation for someone who just received the
    /// program, not as scientifically tuned values. Every field not set
    /// explicitly below falls back to GenerationPreset's own default
    /// (matching StageRandom's own slider defaults), so each preset only
    /// needs to spell out what actually distinguishes it.</summary>
    public static IReadOnlyList<BuiltInPresetDef> BuiltIn { get; } = new List<BuiltInPresetDef>
    {
        new("random.preset.builtin.smallIsland", new GenerationPreset
        {
            WaterPercent = 70,
            TargetLandProvinces = 10,
            IslandCount = 1,
            Obscurity = 3,
            MountainAmount = 0.3,
            CoastDetail = 0.35,
            GenerateRivers = false,
            RiverCount = 3,
            LakeFrequency = 0.2,
            AutoRegionCount = 1,
            ModifierFrequency = 0.05,
        }),
        new("random.preset.builtin.largeIsland", new GenerationPreset
        {
            WaterPercent = 60,
            TargetLandProvinces = 60,
            IslandCount = 1,
            Obscurity = 3,
            MountainAmount = 0.4,
            CoastDetail = 0.3,
            GenerateRivers = true,
            RiverCount = 6,
            LakeFrequency = 0.3,
            AutoRegionCount = 0,
            ModifierFrequency = 0.05,
        }),
        new("random.preset.builtin.archipelago", new GenerationPreset
        {
            WaterPercent = 78,
            TargetLandProvinces = 220,
            IslandCount = 10,
            Obscurity = 4,
            MountainAmount = 0.3,
            CoastDetail = 0.4,
            GenerateRivers = false,
            RiverCount = 3,
            LakeFrequency = 0.1,
            StraitFrequency = 0.35,
            StraitMaxDistance = 250,
            StraitMinSpacing = 48,
            StraitMaxPerIsland = 3,
            AutoRegionCount = 0,
            ModifierFrequency = 0.08,
        }),
        new("random.preset.builtin.smallContinent", new GenerationPreset
        {
            WaterPercent = 55,
            TargetLandProvinces = 420,
            IslandCount = 2,
            Obscurity = 6.5,
            MountainAmount = 0.4,
            CoastDetail = 0.65,
            GenerateRivers = true,
            RiverCount = 8,
            LakeFrequency = 0.35,
            AutoRegionCount = 2,
            ModifierFrequency = 0.05,
        }),
        new("random.preset.builtin.largeContinent", new GenerationPreset
        {
            WaterPercent = 40,
            TargetLandProvinces = 750,
            IslandCount = 3,
            Obscurity = 7,
            MountainAmount = 0.45,
            CoastDetail = 0.7,
            GenerateRivers = true,
            RiverCount = 14,
            LakeFrequency = 0.35,
            AutoRegionCount = 6,
            ModifierFrequency = 0.05,
            CapAt1000 = true,
        }),
        new("random.preset.builtin.mountainousWilderness", new GenerationPreset
        {
            WaterPercent = 50,
            TargetLandProvinces = 160,
            IslandCount = 2,
            Obscurity = 7,
            MountainAmount = 0.65,
            CoastDetail = 0.7,
            GenerateRivers = true,
            RiverCount = 5,
            LakeFrequency = 0.4,
            WastelandByHeight = true,
            WastelandHeightThreshold = 150,
            AutoRegionCount = 1,
            ModifierFrequency = 0.1,
        }),
    };
}
