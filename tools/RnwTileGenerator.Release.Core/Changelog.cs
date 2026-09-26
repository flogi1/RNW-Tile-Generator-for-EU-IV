using System.Text;
using System.Text.RegularExpressions;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Release;

/// <summary>
/// <c>Readme/Changelog.txt</c> (Spec "Changelog.txt"): oben der Block "Unreleased" mit den Gruppen New, Changed, Fixed, darunter die Versionen
/// <c>v&lt;x.y.z&gt; (TT.MM.JJJJ)</c>, neueste zuerst. Ausgabe immer CRLF; ältere Versionen werden nur durchgereicht.
/// </summary>
public static class Changelog
{
    public const string Head = "Unreleased";

    public static readonly IReadOnlyList<string> Groups = ["New", "Changed", "Fixed"];

    public const string EmptyUnreleased = "Unreleased\r\nNew\r\nChanged\r\nFixed\r\n";

    private static readonly Regex VersionHeading = new(@"^v\d+\.\d+\.\d+ \(\d{2}\.\d{2}\.\d{4}\)$", RegexOptions.CultureInvariant);

    /// <summary>Text vom Kopf "Unreleased" bis vor die erste Versionsüberschrift, ohne abschließende Leerzeilen, mit CRLF am Ende.</summary>
    public static string UnreleasedBlock(string fileText)
    {
        var lines = Lines(fileText);
        var end = lines.FindIndex(IsVersionHeading);
        return Join(TrimBlankEnd(end < 0 ? lines : lines.Take(end).ToList()));
    }

    /// <summary>Was einen Release sperrt; leer = in Ordnung.</summary>
    public static IReadOnlyList<string> Problems(string unreleasedBlock, string fileText, AppVersion version)
    {
        var problems = new List<string>();
        var lines = Clean(unreleasedBlock);
        if (lines.Count == 0 || lines[0] != Head)
        {
            problems.Add($"Der Changelog muss mit der Zeile \"{Head}\" beginnen.");
        }

        string? group = null;
        var entries = 0;
        foreach (var line in lines.Skip(lines.Count > 0 && lines[0] == Head ? 1 : 0))
        {
            if (Groups.Contains(line))
            {
                group = line;
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("  ", StringComparison.Ordinal))
            {
                if (group is null)
                {
                    problems.Add($"Eintrag außerhalb einer Gruppe: {line.Trim()}");
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    entries++;
                }
            }
            else
            {
                problems.Add($"Unbekannte Zeile oder Gruppe \"{line}\". Erlaubt sind {string.Join(", ", Groups)} und Einträge mit \"- \".");
            }
        }

        if (entries == 0)
        {
            problems.Add("Der Block \"Unreleased\" hat keinen Eintrag.");
        }

        if (Lines(fileText).Any(line => line.StartsWith($"v{version} (", StringComparison.Ordinal) && IsVersionHeading(line)))
        {
            problems.Add($"Version {version} steht schon im Changelog.");
        }

        return problems;
    }

    /// <summary>Neue Datei: leerer Block "Unreleased", dann die Version mit den nicht leeren Gruppen, dann die älteren Versionen.</summary>
    public static string Release(string fileText, string unreleasedBlock, AppVersion version, DateOnly date)
    {
        var section = new List<string> { $"v{version} ({date:dd.MM.yyyy})" };
        var byGroup = Groups.ToDictionary(g => g, _ => new List<string>());
        string? group = null;
        foreach (var line in Clean(unreleasedBlock))
        {
            if (Groups.Contains(line))
            {
                group = line;
            }
            else if (group is not null && line != Head)
            {
                byGroup[group].Add(line);
            }
        }

        foreach (var name in Groups.Where(g => byGroup[g].Count > 0))
        {
            section.Add(name);
            section.AddRange(byGroup[name]);
        }

        var lines = Lines(fileText);
        var start = lines.FindIndex(IsVersionHeading);
        var older = start < 0 ? new List<string>() : TrimBlankEnd(lines.Skip(start).ToList());
        var result = new StringBuilder(EmptyUnreleased).Append("\r\n").Append(Join(section));
        if (older.Count > 0)
        {
            result.Append("\r\n").Append(Join(older));
        }

        return result.ToString();
    }

    /// <summary>Gruppen und Einträge einer Version ohne Überschrift, CRLF; leer, wenn es die Version nicht gibt.</summary>
    public static string NotesFor(string fileText, AppVersion version)
    {
        var lines = Lines(fileText);
        var start = lines.FindIndex(line => IsVersionHeading(line) && line.StartsWith($"v{version} (", StringComparison.Ordinal));
        if (start < 0)
        {
            return string.Empty;
        }

        var body = lines.Skip(start + 1).TakeWhile(line => !IsVersionHeading(line)).ToList();
        return Join(TrimBlankEnd(body));
    }

    private static bool IsVersionHeading(string line) => VersionHeading.IsMatch(line);

    /// <summary>Zeilen ohne Leerraum am Ende; Folgezeilen behalten ihre Einrückung.</summary>
    private static List<string> Lines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(line => line.TrimEnd(' ', '\t')).ToList();

    /// <summary>Zeilen des Blocks ohne Leerzeilen.</summary>
    private static List<string> Clean(string block) => Lines(block).Where(line => line.Length > 0).ToList();

    private static List<string> TrimBlankEnd(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string Join(IEnumerable<string> lines) => string.Concat(lines.Select(line => line + "\r\n"));
}
