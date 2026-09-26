using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Release;

/// <summary>Paths in the repo (root folder with <c>RnwTileGenerator.sln</c>) and the key file. Ported from PMT.</summary>
public sealed record ReleasePaths(string Root)
{
    public const string SolutionName = "RnwTileGenerator.sln";

    public const string AppProjectRelative = @"RnwTileGenerator.App\RnwTileGenerator.App.csproj";

    /// <summary>Holds the bug report address and the Sponsors link; a real release refuses "PLACEHOLDER" in it.</summary>
    public const string AppInfoRelative = @"RnwTileGenerator.App\AppInfo.cs";

    public static string KeyFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RnwTileGenerator-Release", "release-key.p8");

    public string Solution => Path.Combine(Root, SolutionName);

    public string AppProject => Path.Combine(Root, AppProjectRelative);

    public string AppInfo => Path.Combine(Root, AppInfoRelative);

    public string Changelog => Path.Combine(Root, ReadmeFiles.Folder, ReadmeFiles.ChangelogName);

    /// <summary>Changelog path relative to the repo with "/", for git.</summary>
    public static string ChangelogRelative => ReadmeFiles.Folder + "/" + ReadmeFiles.ChangelogName;

    public string OutputFor(AppVersion version) => Path.Combine(Root, "release-output", version.ToString());

    public string AppOutputFor(AppVersion version) => Path.Combine(OutputFor(version), "app");

    /// <summary>Walks up from the folder to the one containing <c>RnwTileGenerator.sln</c>; null without a hit.</summary>
    public static string? FindRoot(string startFolder)
    {
        for (var folder = new DirectoryInfo(Path.GetFullPath(startFolder)); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, SolutionName)))
            {
                return folder.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Is a source of the tool newer than the exe? Sources = <c>*.cs</c>/<c>*.csproj</c> (without bin/obj) under
    /// <c>tools\RnwTileGenerator.Release*</c> and under <c>RnwTileGenerator.Updates</c>, whose public key the tool bakes in.
    /// </summary>
    public bool ToolSourcesNewerThan(DateTime exeWriteUtc)
    {
        var tools = Path.Combine(Root, "tools");
        var folders = Directory.Exists(tools)
            ? Directory.EnumerateDirectories(tools, "RnwTileGenerator.Release*")
                .Where(folder => !Path.GetFileName(folder).Equals("RnwTileGenerator.ReleaseSigner", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];
        var updates = Path.Combine(Root, "RnwTileGenerator.Updates");
        if (Directory.Exists(updates))
        {
            folders.Add(updates);
        }

        return folders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(file => !IsBuildOutput(file))
            .Any(file => File.GetLastWriteTimeUtc(file) > exeWriteUtc);
    }

    private static bool IsBuildOutput(string file) =>
        file.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase));
}
