namespace RnwTileGenerator.Updates;

/// <summary>Alle Ablageorte des Selbst-Updates. Für Tests auf einen Temp-Ordner umlenkbar.</summary>
public sealed class UpdatePaths(string root)
{
    public const string BackupFolderName = ".update-backup";

    public const string ExecutableName = "RnwTileGenerator.exe";

    /// <summary>Liste der obersten Einträge, die zu RNW gehören; nur diese fasst ein Update an. Liegt in jedem Release-Paket.</summary>
    public const string InstallListName = "rnw-install-files.txt";

    /// <summary>%LocalAppData%\RnwTileGenerator\updates (Local statt Roaming: Pakete sind groß und rechnergebunden).</summary>
    public static UpdatePaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RnwTileGenerator", "updates"));

    public string Root => root;

    public string LogFile => Path.Combine(root, "update.log");

    public string VersionFolder(AppVersion version) => Path.Combine(root, version.ToString());

    public string DownloadFolder(AppVersion version) => Path.Combine(VersionFolder(version), "download");

    public string StagingFolder(AppVersion version) => Path.Combine(VersionFolder(version), "staging");

    /// <summary>Startsignal: Die neue Version schreibt es, sobald ihr Hauptfenster steht.</summary>
    public string Marker(AppVersion version) => Path.Combine(VersionFolder(version), "started.marker");
}
