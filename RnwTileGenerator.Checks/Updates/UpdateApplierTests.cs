using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class UpdateApplierTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    private string Target => _temp.Combine("Programm Ä");

    private string Staging => _temp.Combine("staging");

    private UpdateLog Log => new(_temp.Combine("update.log"));

    private void CreateOldAndNew()
    {
        _temp.WriteFile(Path.Combine("Programm Ä", UpdatePaths.InstallListName), "RnwTileGenerator.exe\nnur-alt.dll\nprofiles");
        _temp.WriteFile(Path.Combine("staging", UpdatePaths.InstallListName), "RnwTileGenerator.exe\nlocales\nprofiles");
        _temp.WriteFile(Path.Combine("Programm Ä", UpdatePaths.ExecutableName), "alt");
        _temp.WriteFile(Path.Combine("Programm Ä", "nur-alt.dll"), "alt");
        _temp.WriteFile(Path.Combine("Programm Ä", "profiles", "eu4.json"), "alt");
        _temp.WriteFile(Path.Combine("staging", UpdatePaths.ExecutableName), "neu");
        _temp.WriteFile(Path.Combine("staging", "profiles", "eu4.json"), "neu");
        _temp.WriteFile(Path.Combine("staging", "locales", "de.json"), "neu");
    }

    private Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(folder, file).StartsWith(UpdatePaths.BackupFolderName, StringComparison.Ordinal))
            .ToDictionary(file => Path.GetRelativePath(folder, file), File.ReadAllText);

    [Fact]
    public void Tausch_ersetzt_den_Ordnerinhalt_und_sichert_den_alten()
    {
        CreateOldAndNew();
        var old = Snapshot(Target);

        new UpdateApplier(Log, retryDelay: TimeSpan.Zero).Swap(Target, Staging);

        Assert.Equal(Snapshot(Staging), Snapshot(Target));
        Assert.Equal(old, Snapshot(Path.Combine(Target, UpdatePaths.BackupFolderName)));
    }

    [Fact]
    public void Fehler_beim_Kopieren_stellt_den_alten_Stand_her()
    {
        CreateOldAndNew();
        var old = Snapshot(Target);
        var applier = new UpdateApplier(Log, retryDelay: TimeSpan.Zero)
        {
            BeforeCopyFile = relative => { if (relative.StartsWith("profiles", StringComparison.Ordinal)) throw new IOException("Platte voll"); },
        };

        var error = Assert.Throws<UpdateApplyException>(() => applier.Swap(Target, Staging));

        Assert.True(error.RolledBack);
        Assert.Equal(old, Snapshot(Target));
        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
    }

    [Fact]
    public void Gesperrte_Datei_bricht_nach_den_Versuchen_ab_und_laesst_alles_wie_es_war()
    {
        CreateOldAndNew();
        var old = Snapshot(Target);
        using (File.Open(Path.Combine(Target, "nur-alt.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.Throws<UpdateApplyException>(() => new UpdateApplier(Log, attempts: 2, retryDelay: TimeSpan.Zero).Swap(Target, Staging));
            Assert.True(error.RolledBack);
        }

        Assert.Equal(old, Snapshot(Target));
    }

    [Fact]
    public void Rueckfall_nach_erfolgreichem_Tausch_stellt_den_alten_Stand_her()
    {
        CreateOldAndNew();
        var old = Snapshot(Target);
        var applier = new UpdateApplier(Log, retryDelay: TimeSpan.Zero);
        applier.Swap(Target, Staging);

        applier.Rollback(Target);

        Assert.Equal(old, Snapshot(Target));
        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
    }

    [Fact]
    public void Alte_Sicherung_wird_vor_dem_Tausch_ersetzt()
    {
        CreateOldAndNew();
        _temp.WriteFile(Path.Combine("Programm Ä", UpdatePaths.BackupFolderName, "uralt.dll"), "uralt");
        var old = Snapshot(Target);
        var applier = new UpdateApplier(Log, retryDelay: TimeSpan.Zero);

        applier.Swap(Target, Staging);
        Assert.False(File.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName, "uralt.dll")));
        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName, UpdatePaths.BackupFolderName)));

        applier.Rollback(Target);
        Assert.Equal(old, Snapshot(Target));
    }
    [Fact]
    public void Aufraeumen_loescht_Sicherung_und_alte_Update_Ordner()
    {
        _temp.WriteFile(Path.Combine("Programm Ä", UpdatePaths.BackupFolderName, "x.dll"), "x");
        var paths = new UpdatePaths(_temp.Combine("updates"));
        foreach (var name in new[] { "0.1.0", "0.2.0", "0.3.0", "fremd" })
        {
            _temp.WriteFile(Path.Combine("updates", name, "staging", "a.txt"), "a");
        }

        _temp.WriteFile(Path.Combine("updates", "update.log"), "log");

        UpdateApplier.CleanupAfterStart(Target, paths, new AppVersion(0, 2, 0), Log);

        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
        Assert.False(Directory.Exists(paths.VersionFolder(new AppVersion(0, 1, 0))));
        Assert.False(Directory.Exists(paths.VersionFolder(new AppVersion(0, 2, 0))));
        Assert.True(Directory.Exists(paths.VersionFolder(new AppVersion(0, 3, 0))));
        Assert.True(Directory.Exists(_temp.Combine("updates", "fremd")));
        Assert.True(File.Exists(paths.LogFile));
    }

    [Fact]
    public void Fremde_Dateien_im_Programmordner_bleiben_immer_erhalten()
    {
        CreateOldAndNew();
        var userFile = _temp.WriteFile(Path.Combine("Programm Ä", "meine-notizen.txt"), "wichtig");
        var applier = new UpdateApplier(Log, retryDelay: TimeSpan.Zero);

        applier.Swap(Target, Staging);
        Assert.Equal("wichtig", File.ReadAllText(userFile));
        applier.Rollback(Target);
        Assert.Equal("wichtig", File.ReadAllText(userFile));
        applier.Swap(Target, Staging);
        UpdateApplier.CleanupAfterStart(Target, new UpdatePaths(_temp.Combine("updates")), new AppVersion(9, 9, 9), Log);

        Assert.Equal("wichtig", File.ReadAllText(userFile));
        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(Target, UpdatePaths.ExecutableName)));
    }

    [Fact]
    public void Ohne_Dateiliste_wird_nichts_angefasst()
    {
        CreateOldAndNew();
        File.Delete(Path.Combine(Target, UpdatePaths.InstallListName));
        var old = Snapshot(Target);

        var error = Assert.Throws<UpdateApplyException>(() => new UpdateApplier(Log, retryDelay: TimeSpan.Zero).Swap(Target, Staging));

        Assert.True(error.RolledBack);
        Assert.Equal(old, Snapshot(Target));
        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
    }

    [Fact]
    public void Fremde_Datei_mit_dem_Namen_eines_neuen_Eintrags_verhindert_den_Tausch_ohne_Aenderung()
    {
        CreateOldAndNew();
        var userFile = _temp.WriteFile(Path.Combine("Programm Ä", "locales"), "meine Datei");
        var old = Snapshot(Target);

        var error = Assert.Throws<UpdateApplyException>(() => new UpdateApplier(Log, retryDelay: TimeSpan.Zero).Swap(Target, Staging));

        Assert.True(error.RolledBack);
        Assert.Equal("meine Datei", File.ReadAllText(userFile));
        Assert.Equal(old, Snapshot(Target));
        Assert.False(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
    }

    [Fact]
    public void Rueckfall_ohne_Sicherung_meldet_einen_Fehler_statt_Erfolg()
    {
        CreateOldAndNew();
        var applier = new UpdateApplier(Log, retryDelay: TimeSpan.Zero);
        applier.Swap(Target, Staging);
        Directory.Delete(Path.Combine(Target, UpdatePaths.BackupFolderName), recursive: true);

        Assert.ThrowsAny<IOException>(() => applier.Rollback(Target));
    }

    [Fact]
    public void Aufraeumen_wartet_nicht_und_loescht_nichts_waehrend_ein_Update_laeuft()
    {
        _temp.WriteFile(Path.Combine("Programm Ä", UpdatePaths.BackupFolderName, "x.dll"), "x");
        var paths = new UpdatePaths(_temp.Combine("updates"));
        _temp.WriteFile(Path.Combine("updates", "0.1.0", "staging", "a.txt"), "a");

        using (UpdateLock.TryAcquire(paths, Target))
        {
            UpdateApplier.CleanupAfterStart(Target, paths, new AppVersion(0, 2, 0), Log);
        }

        Assert.True(Directory.Exists(Path.Combine(Target, UpdatePaths.BackupFolderName)));
        Assert.True(Directory.Exists(paths.VersionFolder(new AppVersion(0, 1, 0))));
    }

    [Fact]
    public void Update_Sperre_gilt_pro_Programmordner_und_endet_mit_Dispose()
    {
        var paths = new UpdatePaths(_temp.Combine("updates"));
        var first = UpdateLock.TryAcquire(paths, Target);

        Assert.NotNull(first);
        Assert.Null(UpdateLock.TryAcquire(paths, Target.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.True(UpdateLock.IsHeld(paths, Target));
        using (var other = UpdateLock.TryAcquire(paths, _temp.Combine("anderer Ordner")))
        {
            Assert.NotNull(other);
        }

        first!.Dispose();
        Assert.False(UpdateLock.IsHeld(paths, Target));
        using var again = UpdateLock.TryAcquire(paths, Target);
        Assert.NotNull(again);
    }

    [Fact]
    public void CanWrite_erkennt_beschreibbaren_Ordner()
    {
        Directory.CreateDirectory(Target);
        Assert.True(UpdateApplier.CanWrite(Target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target));
        Assert.False(UpdateApplier.CanWrite(_temp.Combine("gibt es nicht")));
    }
}
