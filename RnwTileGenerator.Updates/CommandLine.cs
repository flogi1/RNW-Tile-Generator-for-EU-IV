using System.Text;

namespace RnwTileGenerator.Updates;

/// <summary>Lesen und Bauen von Kommandozeilen im Windows-Format.</summary>
public static class CommandLine
{
    /// <summary>
    /// Liest <c>--name wert</c>-Paare ab <paramref name="start"/>. Ein <c>--name</c> ohne folgenden Wert bekommt "". Unbekannte Namen
    /// landen einfach mit im Ergebnis; der Aufrufer fragt nur die ab, die er kennt. Doppelte Namen: der letzte gilt.
    /// </summary>
    public static Dictionary<string, string> ReadOptions(IReadOnlyList<string> args, int start)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < args.Count; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
            options[name] = value;
        }

        return options;
    }

    /// <summary>Setzt Argumente so zusammen, dass Windows (CommandLineToArgvW) sie genau so wieder zerlegt.</summary>
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }

        var builder = new StringBuilder("\"");
        var i = 0;
        while (true)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == arg.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (arg[i] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(arg[i]);
            }

            i++;
        }

        return builder.Append('"').ToString();
    }
}
