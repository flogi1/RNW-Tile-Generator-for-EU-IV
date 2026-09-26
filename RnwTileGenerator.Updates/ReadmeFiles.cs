namespace RnwTileGenerator.Updates;

/// <summary>
/// Lese-Dateien für Nutzer im Ordner <c>Readme</c>: im Repo unter <c>Readme/</c>, im Programmordner von RNW unter <c>Readme\</c> neben der exe
/// (die App-csproj kopiert sie beim Build und Publish dorthin, damit sie mit jedem Update mitkommen).
/// </summary>
public static class ReadmeFiles
{
    public const string Folder = "Readme";

    public const string ChangelogName = "Changelog.txt";

    public static string ChangelogIn(string baseDirectory) => Path.Combine(baseDirectory, Folder, ChangelogName);
}
