using System.Globalization;

namespace RnwTileGenerator.Updates;

/// <summary>
/// Protokoll aller Update-Schritte (Vorbereitung, Update-Modus, erhöhter Schritt). Wirft nie: Schreibfehler, etwa weil ein zweiter
/// Prozess gerade schreibt, werden verschluckt. Wird die Datei größer als <c>maxBytes</c>, fällt die ältere Hälfte weg.
/// </summary>
public sealed class UpdateLog(string path, long maxBytes = 1_048_576)
{
    private readonly object _gate = new();

    public string Path => path;

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                TrimIfTooLarge();
                var line = string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
                File.AppendAllText(path, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void TrimIfTooLarge()
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= maxBytes)
        {
            return;
        }

        var text = File.ReadAllText(path);
        var half = text[(text.Length / 2)..];
        var lineStart = half.IndexOf('\n');
        File.WriteAllText(path, lineStart >= 0 ? half[(lineStart + 1)..] : half);
    }
}
