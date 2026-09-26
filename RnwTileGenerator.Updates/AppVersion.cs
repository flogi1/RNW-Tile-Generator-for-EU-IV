namespace RnwTileGenerator.Updates;

/// <summary>
/// Dreiteilige Versionsnummer (Major.Minor.Patch) für den Update-Vergleich. Ein führendes "v" sowie
/// Zusätze wie "-beta.1" oder "+abc123" (Build-Metadaten) werden beim Parsen ignoriert; Vorabversionen
/// werden nicht über die Nummer, sondern über das <c>prerelease</c>-Flag der GitHub-Release-Daten erkannt.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        var cut = span.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            span = span[..cut];
        }

        var parts = span.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            // Ein vierter Teil (Revision, wie bei Assembly-Versionen) wird ignoriert.
            if (i < 3)
            {
                numbers[i] = number;
            }
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
