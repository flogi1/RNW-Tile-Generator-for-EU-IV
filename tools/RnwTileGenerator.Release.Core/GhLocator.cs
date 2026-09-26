namespace RnwTileGenerator.Release;

/// <summary>
/// Finds the GitHub CLI like PMT's release tool: PATH, the default install folder, then the copy PMT downloads into its
/// tools folder. RNW itself never installs gh; the user runs "gh auth login" once.
/// </summary>
public static class GhLocator
{
    public static string? Find()
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(folder => Path.Combine(folder, "gh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ParadoxModTool", "tools", "gh", "bin", "gh.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }
}
