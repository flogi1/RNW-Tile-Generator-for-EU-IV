using System.Security.Cryptography;
using System.Text;

namespace RnwTileGenerator.Updates;

/// <summary>
/// Sperre pro Programmordner: Nur ein Update-Vorgang läuft gleichzeitig, und das Aufräumen beim Start fasst währenddessen nichts an.
/// Eine exklusiv geöffnete Datei unter <see cref="UpdatePaths.Root"/> statt eines Mutex, weil der Update-Ablauf async über Threads
/// wechselt; stirbt der Prozess, gibt Windows die Datei von selbst frei.
/// </summary>
public sealed class UpdateLock : IDisposable
{
    private readonly FileStream _file;

    private UpdateLock(FileStream file)
    {
        _file = file;
    }

    /// <summary><see langword="null"/>, wenn schon ein anderer Vorgang die Sperre für diesen Ordner hält.</summary>
    public static UpdateLock? TryAcquire(UpdatePaths paths, string installFolder)
    {
        try
        {
            Directory.CreateDirectory(paths.Root);
            var file = new FileStream(LockFile(paths, installFolder), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return new UpdateLock(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static bool IsHeld(UpdatePaths paths, string installFolder)
    {
        using var probe = TryAcquire(paths, installFolder);
        return probe is null;
    }

    public void Dispose() => _file.Dispose();

    /// <summary>Ein Dateiname je Programmordner (Groß-/Kleinschreibung und abschließender Trenner zählen nicht).</summary>
    private static string LockFile(UpdatePaths paths, string installFolder)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..32].ToLowerInvariant();
        return Path.Combine(paths.Root, $"update-{hash}.lock");
    }
}
