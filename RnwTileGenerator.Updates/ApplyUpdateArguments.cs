using System.Globalization;

namespace RnwTileGenerator.Updates;

/// <summary>
/// Aufruf des Update-Modus: <c>RnwTileGenerator.exe --apply-update --protocol 1 --target … --wait-pid … --from-version …
/// --to-version … [--open …]</c>. Diese Kommandozeile ist ein Vertrag über alle Versionen: Die alte Version ruft die neue auf. Parameter
/// dürfen dazukommen, vorhandene dürfen nie wegfallen oder ihre Bedeutung ändern; Unbekanntes wird ignoriert.
/// <c>--open</c> nennt das Projekt, das nach dem Neustart wieder geöffnet wird (portiert aus PMT, dort <c>--game</c>/<c>--lang</c>).
/// </summary>
public sealed record ApplyUpdateArguments(int Protocol, string Target, int WaitPid, AppVersion FromVersion, AppVersion ToVersion, string? OpenProject)
{
    public const string Switch = "--apply-update";

    public const int CurrentProtocol = 1;

    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string>
        {
            Switch,
            "--protocol", Protocol.ToString(CultureInfo.InvariantCulture),
            "--target", Target,
            "--wait-pid", WaitPid.ToString(CultureInfo.InvariantCulture),
            "--from-version", FromVersion.ToString(),
            "--to-version", ToVersion.ToString(),
        };
        if (!string.IsNullOrEmpty(OpenProject))
        {
            args.AddRange([UpdateStartArguments.Open, OpenProject]);
        }

        return args;
    }

    public static ApplyUpdateArguments? Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != Switch)
        {
            return null;
        }

        var options = CommandLine.ReadOptions(args, 1);
        if (!TryInt(options, "--protocol", out var protocol) || protocol < 1
            || !options.TryGetValue("--target", out var target) || !Path.IsPathFullyQualified(target)
            || !TryInt(options, "--wait-pid", out var pid)
            || !AppVersion.TryParse(options.GetValueOrDefault("--from-version"), out var from)
            || !AppVersion.TryParse(options.GetValueOrDefault("--to-version"), out var to))
        {
            return null;
        }

        return new ApplyUpdateArguments(protocol, Path.TrimEndingDirectorySeparator(target), pid, from, to, UpdateStartArguments.NonEmpty(options, UpdateStartArguments.Open));
    }

    private static bool TryInt(Dictionary<string, string> options, string name, out int value)
    {
        value = 0;
        return options.TryGetValue(name, out var text) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>Was die normale App beim Start über ein vorangegangenes Update erfährt, plus das wieder zu öffnende Projekt.</summary>
public sealed record UpdateStartInfo(AppVersion? Applied, AppVersion? From, AppVersion? Failed, AppVersion? Cancelled, string? OpenProject)
{
    public bool IsUpdateStart => Applied is not null || Failed is not null || Cancelled is not null;
}

/// <summary>Start-Argumente der normalen App; der Update-Modus startet sie damit wieder (immer mit <c>--open</c> davor, falls gesetzt).</summary>
public static class UpdateStartArguments
{
    /// <summary>Projektdatei (.rnwproj), die nach dem Start geöffnet wird. Auch ohne Update nutzbar.</summary>
    public const string Open = "--open";

    public const string Applied = "--update-applied";

    public const string From = "--update-from";

    public const string Failed = "--update-failed";

    public const string Cancelled = "--update-cancelled";

    public static IReadOnlyList<string> ForApplied(string? openProject, AppVersion to, AppVersion from) =>
        [.. OpenArgs(openProject), Applied, to.ToString(), From, from.ToString()];

    public static IReadOnlyList<string> ForFailed(string? openProject, AppVersion to) => [.. OpenArgs(openProject), Failed, to.ToString()];

    public static IReadOnlyList<string> ForCancelled(string? openProject, AppVersion to) => [.. OpenArgs(openProject), Cancelled, to.ToString()];

    public static UpdateStartInfo Parse(IReadOnlyList<string> args)
    {
        var options = CommandLine.ReadOptions(args, 0);
        return new UpdateStartInfo(Version(options, Applied), Version(options, From), Version(options, Failed), Version(options, Cancelled), NonEmpty(options, Open));
    }

    /// <summary>Die Start-Argumente ohne die Update-Schalter und ihre Werte.</summary>
    public static string[] WithoutUpdateArguments(IReadOnlyList<string> args)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is Applied or From or Failed or Cancelled)
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            result.Add(args[i]);
        }

        return result.ToArray();
    }

    internal static string? NonEmpty(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static IEnumerable<string> OpenArgs(string? openProject) => string.IsNullOrEmpty(openProject) ? [] : [Open, openProject];

    private static AppVersion? Version(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var text) && AppVersion.TryParse(text, out var version) ? version : null;
}
