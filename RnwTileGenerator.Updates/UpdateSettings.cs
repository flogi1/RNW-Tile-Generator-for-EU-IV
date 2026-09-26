using System.Text.Json;

namespace RnwTileGenerator.Updates;

/// <summary>
/// Update settings of the user (not of an installation): check at startup, time of the last check, skipped version.
/// Stored in %LocalAppData%\RnwTileGenerator\update_settings.json. Load never throws (missing or broken file = defaults),
/// Save swallows IO errors - a lost setting only means one extra update check.
/// </summary>
public sealed record UpdateSettings(bool CheckForUpdatesOnStartup = true, DateTime? LastUpdateCheckUtc = null, string? SkippedUpdateVersion = null)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RnwTileGenerator", "update_settings.json");

    public static UpdateSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateSettings>(File.ReadAllBytes(path)) ?? new UpdateSettings() : new UpdateSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new UpdateSettings();
        }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
