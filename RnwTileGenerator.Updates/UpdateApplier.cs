namespace RnwTileGenerator.Updates;

public sealed class UpdateApplyException(string message, bool rolledBack, Exception? inner) : Exception(message, inner)
{
    /// <summary><see langword="true"/>: Der alte Stand ist wiederhergestellt. <see langword="false"/>: Er liegt nur noch in <c>.update-backup</c>.</summary>
    public bool RolledBack { get; } = rolledBack;
}

/// <summary>
/// Dateischritte des Updates im Programmordner: alles nach <c>.update-backup</c> verschieben (Umbenennen auf demselben Laufwerk),
/// neue Dateien hineinkopieren; bei Fehlern den alten Stand zurückholen. Braucht nur Schreibrecht im Programmordner selbst.
/// </summary>
public sealed class UpdateApplier(UpdateLog log, int attempts = 10, TimeSpan? retryDelay = null)
{
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(500);

    /// <summary>Nur für Tests: wird vor jeder kopierten Datei mit dem relativen Pfad aufgerufen.</summary>
    public Action<string>? BeforeCopyFile { get; init; }

    public void Swap(string target, string staging)
    {
        var backup = Path.Combine(target, UpdatePaths.BackupFolderName);
        log.Write($"Tausch: {staging} -> {target}");
        IReadOnlyList<string> oldEntries;
        IReadOnlyList<string> newEntries;
        try
        {
            // Ohne Dateiliste (z. B. Entwicklungsordner oder ein Ordner, in den der Nutzer das ZIP mit anderen Dateien entpackt hat)
            // wäre unklar, was zu RNW gehört: dann gar nichts anfassen.
            if (ReadInstallList(target) is not { } oldList || ReadInstallList(staging) is not { } newList)
            {
                log.Write("Dateiliste fehlt, Tausch abgelehnt.");
                throw new UpdateApplyException($"{UpdatePaths.InstallListName} fehlt.", rolledBack: true, null);
            }

            oldEntries = oldList;
            newEntries = newList;

            // Ein neuer Eintrag mit dem Namen einer fremden Datei (nicht in der alten Liste) würde beim Kopieren scheitern, und der Rückfall
            // löschte dann die fremde Datei mit. Deshalb vorher ablehnen, solange noch nichts verändert ist.
            var collisions = newEntries
                .Where(name => !oldEntries.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Where(name => File.Exists(Path.Combine(target, name)) || Directory.Exists(Path.Combine(target, name)))
                .ToList();
            if (collisions.Count > 0)
            {
                log.Write("Fremde Einträge tragen Namen der neuen Version, Tausch abgelehnt: " + string.Join(", ", collisions));
                throw new UpdateApplyException("Fremde Einträge im Programmordner: " + string.Join(", ", collisions), rolledBack: true, null);
            }

            // Rest eines früheren, erfolgreichen Updates: darf weg, bevor die neue Sicherung entsteht.
            if (Directory.Exists(backup))
            {
                Retry(() => Directory.Delete(backup, recursive: true));
            }

            Directory.CreateDirectory(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Tausch nicht vorbereitbar: " + ex.Message);
            throw new UpdateApplyException(ex.Message, rolledBack: true, ex);
        }

        var copying = false;
        try
        {
            foreach (var name in oldEntries)
            {
                var entry = Path.Combine(target, name);
                if (File.Exists(entry) || Directory.Exists(entry))
                {
                    Retry(() => Move(entry, Path.Combine(backup, name)));
                }
            }

            copying = true;
            CopyDirectory(staging, target, staging);
            log.Write("Tausch fertig.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write("Tausch fehlgeschlagen: " + ex.Message);
            try
            {
                // Scheitert schon das Verschieben, wurde noch nichts kopiert: nichts löschen, nur zurückholen.
                Restore(target, copying ? newEntries : Array.Empty<string>());
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                log.Write($"Rückfall fehlgeschlagen, alte Version liegt in {backup}: {rollbackError.Message}");
                throw new UpdateApplyException(ex.Message, rolledBack: false, rollbackError);
            }

            throw new UpdateApplyException(ex.Message, rolledBack: true, ex);
        }
    }

    /// <summary>Nach vollständigem Tausch: Einträge der neuen Version (laut ihrer Dateiliste) löschen, alten Stand zurückholen.</summary>
    public void Rollback(string target) => Restore(target, ReadInstallList(target) ?? Array.Empty<string>());

    /// <summary>
    /// Oberste Einträge, die laut <see cref="UpdatePaths.InstallListName"/> zu RNW gehören, plus die Liste selbst;
    /// <see langword="null"/>, wenn die Liste fehlt. Unsichere Namen (Pfade, "..", der Sicherungsordner) werden übergangen.
    /// </summary>
    public static IReadOnlyList<string>? ReadInstallList(string folder)
    {
        var file = Path.Combine(folder, UpdatePaths.InstallListName);
        if (!File.Exists(file))
        {
            return null;
        }

        var names = File.ReadAllLines(file)
            .Select(line => line.Trim())
            .Where(name => name.Length > 0 && name != "." && name != ".." && name == Path.GetFileName(name)
                           && !string.Equals(name, UpdatePaths.BackupFolderName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        names.Add(UpdatePaths.InstallListName);
        return names;
    }

    private void Restore(string target, IReadOnlyList<string> removeNames)
    {
        var backup = Path.Combine(target, UpdatePaths.BackupFolderName);
        if (!Directory.Exists(backup))
        {
            // Ohne Sicherung gibt es nichts wiederherzustellen; "Erfolg" zu melden hieße, die neue Version als die alte auszugeben.
            log.Write("Rückfall unmöglich: keine Sicherung vorhanden.");
            throw new IOException($"{UpdatePaths.BackupFolderName} fehlt.");
        }

        log.Write("Rückfall auf die Sicherung.");
        foreach (var name in removeNames)
        {
            var entry = Path.Combine(target, name);
            Retry(() => Delete(entry));
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(backup).ToList())
        {
            Retry(() => Move(entry, Path.Combine(target, Path.GetFileName(entry))));
        }

        Retry(() => Directory.Delete(backup, recursive: true));
        log.Write("Rückfall fertig.");
    }

    /// <summary>Legt eine Testdatei an und löscht sie wieder.</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".update-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Beim normalen Start (ohne Update-Argumente): Sicherung im Programmordner und alle Update-Ordner bis zur eigenen Version löschen.
    /// Fehler werden ignoriert und beim nächsten Start erneut versucht.
    /// </summary>
    public static void CleanupAfterStart(string installFolder, UpdatePaths paths, AppVersion current, UpdateLog log)
    {
        // Läuft gerade ein Update für diesen Ordner, gehören Sicherung und Staging ihm.
        using var updateLock = UpdateLock.TryAcquire(paths, installFolder);
        if (updateLock is null)
        {
            log.Write("Aufräumen übersprungen: ein Update läuft.");
            return;
        }

        TryDeleteDirectory(Path.Combine(installFolder, UpdatePaths.BackupFolderName), log);
        if (!Directory.Exists(paths.Root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(paths.Root))
        {
            if (AppVersion.TryParse(Path.GetFileName(folder), out var version) && version <= current)
            {
                TryDeleteDirectory(folder, log);
            }
        }
    }

    private void CopyDirectory(string source, string destination, string root)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            BeforeCopyFile?.Invoke(Path.GetRelativePath(root, file));
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var folder in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)), root);
        }
    }

    private static void Move(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }

    private static void Delete(string entry)
    {
        if (Directory.Exists(entry))
        {
            Directory.Delete(entry, recursive: true);
        }
        else if (File.Exists(entry))
        {
            File.Delete(entry);
        }
    }

    /// <summary>Gesperrte Dateien (Virenscanner, eben beendeter Prozess): mehrere Versuche mit Pause, danach wird der Fehler weitergereicht.</summary>
    private void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < attempts)
            {
                Thread.Sleep(_retryDelay);
            }
        }
    }

    private static void TryDeleteDirectory(string folder, UpdateLog log)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
                log.Write("Aufgeräumt: " + folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Aufräumen später erneut ({folder}): {ex.Message}");
        }
    }
}
