using System;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// Program version and the fixed addresses of the project. The release tool refuses a real release while
/// a value here still carries the placeholder marker (ReleaseRun.Placeholder in tools/RnwTileGenerator.Release.Core;
/// the marker word must not appear anywhere else in this file, not even in a comment).
/// </summary>
public static class AppInfo
{
    /// <summary>From the assembly version, i.e. &lt;Version&gt; in RnwTileGenerator.App.csproj (set by the release tool).</summary>
    public static AppVersion Version { get; } = ReadVersion();

    public const string Repository = "flogi1/RNW-Tile-Generator-for-EU-IV";

    public const string RepositoryUrl = "https://github.com/" + Repository;

    /// <summary>Dedicated address for bug reports sent by email (fallback for users without a GitHub account).</summary>
    public const string BugReportEmail = "flogirnw@gmail.com";

    /// <summary>GitHub Sponsors page opened by the "Donate" menu entry.</summary>
    public const string DonationUrl = "https://github.com/sponsors/flogi1";

    private static AppVersion ReadVersion()
    {
        var version = typeof(AppInfo).Assembly.GetName().Version;
        return version is null ? default : new AppVersion(version.Major, version.Minor, Math.Max(0, version.Build));
    }
}
