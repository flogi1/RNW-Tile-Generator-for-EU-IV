using RnwTileGenerator.App;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.Checks;

/// <summary>Export layout (own tile folder vs. straight into a mod) and preset editing.</summary>
public sealed class ExportAndPresetChecks
{
    [Fact]
    public void Tile_folder_layout_nests_a_folder_named_after_the_tile()
    {
        var paths = TileProject.ResolveExportPaths(@"C:\out", "isle", ExportLayout.TileFolder);
        Assert.Equal(@"C:\out\isle\isle.txt", paths.TxtPath);
        Assert.Equal(@"C:\out\isle\data\isle", paths.DataBasePath);
        Assert.Equal(@"C:\out\isle", paths.TileDir);
    }

    [Theory]
    [InlineData(@"C:\mods\My Tiles")]
    [InlineData(@"C:\mods\My Tiles\")]
    public void Mod_layout_appends_map_random_tiles_to_a_mod_root(string modRoot)
    {
        var paths = TileProject.ResolveExportPaths(modRoot, "isle", ExportLayout.ModFolder);
        Assert.Equal(@"C:\mods\My Tiles\map\random\tiles\isle.txt", paths.TxtPath);
        Assert.Equal(@"C:\mods\My Tiles\map\random\tiles\data\isle", paths.DataBasePath);
        Assert.Equal(@"C:\mods\My Tiles\map\random\tiles", paths.TileDir);
    }

    [Theory]
    [InlineData(@"C:\mods\My Tiles\map\random\tiles")]
    [InlineData(@"C:\mods\My Tiles\Map\Random\Tiles\")]
    public void Mod_layout_keeps_a_path_that_already_is_the_tiles_folder(string tilesDir)
    {
        var paths = TileProject.ResolveExportPaths(tilesDir, "isle", ExportLayout.ModFolder);
        Assert.Equal(Path.Combine(tilesDir.TrimEnd('\\'), "isle.txt"), paths.TxtPath);
        Assert.Equal(Path.Combine(tilesDir.TrimEnd('\\'), "data", "isle"), paths.DataBasePath);
    }

    private static List<NamedPreset> Presets(params string[] names) =>
        names.Select(n => new NamedPreset { Name = n, Values = new GenerationPreset { WaterPercent = n.Length } }).ToList();

    [Fact]
    public void Rename_keeps_position_and_values()
    {
        var list = Presets("a", "bb", "ccc");
        Assert.Equal(PresetRenameResult.Ok, PresetStore.RenameIn(list, "bb", "new"));
        Assert.Equal(new[] { "a", "new", "ccc" }, list.Select(p => p.Name));
        Assert.Equal(2.0, list[1].Values.WaterPercent);
    }

    [Fact]
    public void Rename_rejects_an_existing_name_but_allows_a_case_change()
    {
        var list = Presets("a", "bb");
        Assert.Equal(PresetRenameResult.NameTaken, PresetStore.RenameIn(list, "a", "BB"));
        Assert.Equal(PresetRenameResult.Ok, PresetStore.RenameIn(list, "bb", "BB"));
        Assert.Equal(new[] { "a", "BB" }, list.Select(p => p.Name));
    }

    [Fact]
    public void New_texts_exist_in_all_seven_languages()
    {
        string[] keys =
        [
            "toolbar.export", "toolbar.export.tip", "toolbar.export.validate", "toolbar.provinces.rect", "toolbar.provinces.recolor",
            "toolbar.sf.removeAllStraits", "toolbar.sf.removeAllRegions", "toolbar.sf.removeAllModifiers",
            "random.preset.overwrite", "random.preset.overwrite.tip", "random.preset.confirmOverwrite", "random.preset.cannotOverwriteBuiltIn",
            "random.preset.rename", "random.preset.cannotRenameBuiltIn", "random.preset.nameTaken",
            "export.layout.group", "export.layout.tileFolder", "export.layout.tileFolder.tip", "export.layout.modFolder",
            "export.layout.modFolder.tip", "export.layout.pickMod", "export.layout.modMissing", "export.noFolder", "export.done",
        ];
        var entries = Loc.Entries.ToDictionary(e => e.Key, e => e.Value);
        var missing = keys.SelectMany(k => entries.TryGetValue(k, out var tr)
                ? Loc.AllLanguages.Where(l => !tr.TryGetValue(l, out var t) || string.IsNullOrWhiteSpace(t)).Select(l => $"{k} ({l})")
                : [k + " (missing)"])
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void Rename_reports_a_missing_preset()
    {
        var list = Presets("a");
        Assert.Equal(PresetRenameResult.NotFound, PresetStore.RenameIn(list, "zzz", "b"));
    }
}
