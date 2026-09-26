using System.IO.Compression;
using System.Text.Json;

namespace RnwTileGenerator.Updates;

/// <summary>Bausteine für das Release-Werkzeug: ZIP bauen und einen lokalen Test-Feed beschreiben.</summary>
public static class ReleasePackaging
{
    public static string ZipName(AppVersion version) => $"RnwTileGenerator-{version}-win-x64.zip";

    /// <summary>
    /// Schreibt <see cref="UpdatePaths.InstallListName"/>: die obersten Einträge des Publish-Ordners. Ein Update verschiebt und löscht
    /// im Programmordner nur diese Einträge, damit fremde Dateien des Nutzers dort nie verloren gehen.
    /// </summary>
    public static void WriteInstallList(string folder)
    {
        var names = Directory.EnumerateFileSystemEntries(folder)
            .Select(entry => Path.GetFileName(entry))
            .Where(name => !string.Equals(name, UpdatePaths.InstallListName, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        File.WriteAllLines(Path.Combine(folder, UpdatePaths.InstallListName), names);
    }

    /// <summary>Dateiliste schreiben, dann den Inhalt von <paramref name="folder"/> direkt in die ZIP-Wurzel (Einträge mit "/" als Trenner).</summary>
    public static void CreateZip(string folder, string zipPath)
    {
        WriteInstallList(folder);
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    /// <summary>Schreibt <c>release.json</c> in Form der GitHub-Antwort: alle anderen Dateien des Ordners als Assets.</summary>
    public static void WriteLocalFeed(string folder, AppVersion version)
    {
        var assets = Directory.EnumerateFiles(folder)
            .Where(file => !string.Equals(Path.GetFileName(file), LocalFeedHandler.ReleaseFile, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => new
            {
                name = Path.GetFileName(file),
                browser_download_url = LocalFeedHandler.BaseUrl + Uri.EscapeDataString(Path.GetFileName(file)),
                size = new FileInfo(file).Length,
            })
            .ToList();
        var release = new { tag_name = "v" + version, html_url = LocalFeedHandler.BaseUrl, draft = false, prerelease = false, assets };
        File.WriteAllBytes(Path.Combine(folder, LocalFeedHandler.ReleaseFile), JsonSerializer.SerializeToUtf8Bytes(release, new JsonSerializerOptions { WriteIndented = true }));
    }
}
