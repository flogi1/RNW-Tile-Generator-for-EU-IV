using System.Text.RegularExpressions;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Release;

/// <summary>Version aus <c>&lt;Version&gt;</c> der App-csproj: lesen, nächste vorschlagen, Eingabe prüfen, ersetzen.</summary>
public static class ReleaseVersion
{
    private static readonly Regex VersionElement = new(@"<Version>\s*(?<v>[^<]+?)\s*</Version>", RegexOptions.CultureInvariant);

    private static readonly Regex Plain = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);

    public static AppVersion Current(string csprojText)
    {
        foreach (Match match in VersionElement.Matches(csprojText))
        {
            if (AppVersion.TryParse(match.Groups["v"].Value, out var version))
            {
                return version;
            }
        }

        throw new InvalidDataException("Keine <Version> in der csproj gefunden.");
    }

    public static AppVersion SuggestNext(AppVersion current) => current with { Patch = current.Patch + 1 };

    /// <summary>Null, wenn die Eingabe gültig ist. Eingabe wird getrimmt; nur <c>x.y.z</c>, außer beim Test-Feed größer als die aktuelle.</summary>
    public static string? Problem(string input, AppVersion current, bool localFeed, out AppVersion version)
    {
        version = default;
        var text = input.Trim();
        if (!Plain.IsMatch(text) || !AppVersion.TryParse(text, out version))
        {
            return "Version im Format x.y.z angeben.";
        }

        return !localFeed && version <= current ? $"Version muss größer als {current} sein." : null;
    }

    /// <summary>Ersetzt genau <c>&lt;Version&gt;current&lt;/Version&gt;</c>; alles andere bleibt.</summary>
    public static string WithVersion(string csprojText, AppVersion current, AppVersion next)
    {
        var old = $"<Version>{current}</Version>";
        if (!csprojText.Contains(old, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{old} nicht in der csproj gefunden.");
        }

        return csprojText.Replace(old, $"<Version>{next}</Version>", StringComparison.Ordinal);
    }
}
