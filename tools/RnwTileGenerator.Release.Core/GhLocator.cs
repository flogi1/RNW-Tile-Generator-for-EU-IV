namespace RnwTileGenerator.Release;

/// <summary>
/// Finds the GitHub CLI like PMT's release tool: PATH, the default install folder, then the copy PMT downloads into its
/// tools folder. RNW itself never installs gh; the user runs "gh auth login" once.
/// </summary>
public static class GhLocator
{
    public static string? Find() => Find(
        Environment.GetEnvironmentVariable("PATH"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>PATH entries may be quoted ("C:\Program Files\GitHub CLI"); the quotes are not part of the folder.</summary>
    public static string? Find(string? path, string programFiles, string appData)
    {
        var candidates = (path ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => folder.Trim('"'))
            .Where(folder => folder.Length > 0)
            .Select(folder => Path.Combine(folder, "gh.exe"))
            .Append(Path.Combine(programFiles, "GitHub CLI", "gh.exe"))
            .Append(Path.Combine(appData, "ParadoxModTool", "tools", "gh", "bin", "gh.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
