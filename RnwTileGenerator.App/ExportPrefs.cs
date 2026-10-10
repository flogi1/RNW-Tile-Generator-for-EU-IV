using System;
using System.IO;
using System.Text.Json;
using RnwTileGenerator.Core;

namespace RnwTileGenerator.App;

/// <summary>
/// Remembers the Export tab's target between sessions: the chosen layout and one
/// folder per layout (own tile folder vs. a mod folder), so switching the layout
/// brings back the folder last used for it. Same JSON-next-to-the-exe pattern as
/// GenerationPrefs.cs; an update keeps the file (it is not in the install list).
/// Best-effort: unreadable file means defaults.
/// </summary>
public sealed class ExportPrefs
{
    public ExportLayout Layout { get; set; } = ExportLayout.TileFolder;
    public string? TileFolderPath { get; set; }
    public string? ModFolderPath { get; set; }

    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "export_prefs.json");

    public string? PathFor(ExportLayout layout) => layout == ExportLayout.ModFolder ? ModFolderPath : TileFolderPath;

    public void SetPath(ExportLayout layout, string path)
    {
        if (layout == ExportLayout.ModFolder) ModFolderPath = path;
        else TileFolderPath = path;
    }

    public static ExportPrefs Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ExportPrefs>(File.ReadAllText(FilePath)) ?? new ExportPrefs();
        }
        catch
        {
            // Corrupt or unreadable file - start fresh rather than crash.
        }
        return new ExportPrefs();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best effort - the folder just isn't remembered next launch.
        }
    }
}
