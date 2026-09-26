using System.Security.Cryptography;
using System.Text;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Release;

public enum ReleaseStep
{
    Checks,
    Build,
    Tests,
    Publish,
    Package,
    Sign,
    LocalFeed,
    Commit,
    PublicCommit,
    Push,
    GitHubRelease,
}

public enum StepState
{
    Waiting,
    Running,
    Ok,
    Failed,
    Skipped,
}

public enum SignResult
{
    Ok,
    WrongPassword,
    VerifyFailed,
}

/// <summary>Eingaben eines Laufs. <c>LocalFeedFolder</c> gesetzt = nur lokaler Test-Feed (Debug, ohne Git, Tests, Changelog und Upload).</summary>
public sealed record ReleaseOptions(
    ReleasePaths Paths,
    AppVersion Version,
    string UnreleasedBlock,
    string? LocalFeedFolder,
    DateOnly Today,
    string KeyFile,
    string? GhPath);

/// <summary>
/// Ein Release in Schritten (Spec "Fenster und Ablauf"): Vorprüfungen, Bauen und testen, Signieren, Veröffentlichen. Bis vor dem lokalen Commit
/// lassen sich csproj und Changelog mit <see cref="Revert"/> zurücknehmen; bei Fehler oder Abbruch im Build geschieht das von selbst.
/// </summary>
public sealed class ReleaseRun
{
    public const string Repository = "flogi1/RNW-Tile-Generator-for-EU-IV";

    private const string AppProjectForGit = "RnwTileGenerator.App/RnwTileGenerator.App.csproj";

    private const string AppProjectFolder = "RnwTileGenerator.App";

    /// <summary>The checks project replaces PMT's "dotnet test" (RNW has no NuGet, so no xUnit); exit code 0 = all green.</summary>
    private const string ChecksProject = "RnwTileGenerator.Checks";

    /// <summary>Same single-file publish as the old publish.bat: one exe plus Icons and the reader files.</summary>
    private static readonly string[] SingleFileProperties =
    [
        "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None", "-p:DebugSymbols=false",
    ];

    /// <summary>Marker for values in AppInfo.cs that still have to be filled in (bug address, Sponsors name).</summary>
    public const string Placeholder = "PLACEHOLDER";

    /// <summary>Folders that belong to the repo but are not built (old code copy, notes, the published release tool, output).</summary>
    private static readonly string[] NotBuilt = ["Migration Files", "loco", "release-tool", "release-output", ".git", ".worktrees", ".superpowers"];

    private readonly ReleaseOptions _options;
    private readonly ICommandRunner _runner;
    private readonly string _publicKey;
    private string? _originalCsproj;
    private string? _originalChangelog;
    private bool _committed;
    private string? _publicCommit;

    public ReleaseRun(ReleaseOptions options, ICommandRunner runner)
        : this(options, runner, UpdateKeys.ProductionPublicKey)
    {
    }

    internal ReleaseRun(ReleaseOptions options, ICommandRunner runner, string publicKey)
    {
        _options = options;
        _runner = runner;
        _publicKey = publicKey;
    }

    public event Action<ReleaseStep, StepState>? StepChanged;

    public event Action<string>? Log;

    public bool IsLocalFeed => _options.LocalFeedFolder is not null;

    /// <summary>ZIP, Manifest und Signatur mit Größe, nach dem Signieren.</summary>
    public IReadOnlyList<(string Path, long Bytes)> Files { get; private set; } = [];

    /// <summary>Notizen dieser Version aus dem Changelog (nach dem Build; leer beim Test-Feed).</summary>
    public string Notes { get; private set; } = string.Empty;

    private ReleasePaths Paths => _options.Paths;

    private AppVersion Version => _options.Version;

    private string Configuration => IsLocalFeed ? "Debug" : "Release";

    private string Output => Paths.OutputFor(Version);

    private string Zip => Path.Combine(Output, ReleasePackaging.ZipName(Version));

    private string Manifest => Path.Combine(Output, UpdatePreparer.ManifestName);

    private string Signature => Path.Combine(Output, UpdatePreparer.SignatureName);

    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellation)
    {
        Set(ReleaseStep.Checks, StepState.Running);
        var problems = new List<string>();
        if (!File.Exists(_options.KeyFile))
        {
            problems.Add($"Schlüsseldatei {_options.KeyFile} fehlt. Erst: dotnet run --project tools/RnwTileGenerator.ReleaseSigner -- keygen");
        }

        try
        {
            var current = ReleaseVersion.Current(File.ReadAllText(Paths.AppProject));
            if (ReleaseVersion.Problem(Version.ToString(), current, IsLocalFeed, out _) is { } versionProblem)
            {
                problems.Add(versionProblem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            problems.Add($"Version nicht lesbar: {ex.Message}");
        }

        try
        {
            if (File.ReadAllText(Paths.AppInfo).Contains(Placeholder, StringComparison.Ordinal))
            {
                var text = $"{ReleasePaths.AppInfoRelative} enthält noch {Placeholder} (Bug-Adresse oder Sponsors-Name eintragen).";
                if (IsLocalFeed)
                {
                    Write("WARNUNG: " + text);
                }
                else
                {
                    problems.Add(text);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{ReleasePaths.AppInfoRelative} nicht lesbar: {ex.Message}");
        }

        if (!IsLocalFeed)
        {
            if (!File.Exists(Paths.Changelog))
            {
                problems.Add($"{Paths.Changelog} fehlt.");
            }
            else
            {
                problems.AddRange(Changelog.Problems(_options.UnreleasedBlock, File.ReadAllText(Paths.Changelog), Version));
            }

            var branch = await Git(cancellation, "branch", "--show-current");
            if (branch.Output.FirstOrDefault()?.Trim() != "master")
            {
                problems.Add("Releases nur von master.");
            }

            var dirty = (await Git(cancellation, "status", "--porcelain", "--untracked-files=no")).Output
                .Where(line => line.Length > 3 && line[3..].Trim() != ReleasePaths.ChangelogRelative)
                .Select(line => line[3..].Trim())
                .ToList();
            if (dirty.Count > 0)
            {
                problems.Add($"Arbeitsbaum ist nicht sauber: {string.Join(", ", dirty.Take(5))}. Erst committen oder aufräumen.");
            }

            if ((await Git(cancellation, "remote", "get-url", "origin")).ExitCode != 0)
            {
                problems.Add($"Kein Git-Remote 'origin'. Einrichten: git remote add origin https://github.com/{Repository}.git");
            }

            if (_options.GhPath is not { } gh)
            {
                problems.Add("GitHub CLI (gh.exe) nicht gefunden: weder im PATH noch unter Programme\\GitHub CLI noch im PMT-Werkzeugordner (%APPDATA%\\ParadoxModTool\\tools\\gh).");
            }
            else if ((await Run(gh, cancellation, "auth", "status")).ExitCode != 0)
            {
                problems.Add($"gh ist nicht angemeldet. Erst: '{gh}' auth login");
            }
        }

        foreach (var problem in problems)
        {
            Write("FEHLER: " + problem);
        }

        Set(ReleaseStep.Checks, problems.Count == 0 ? StepState.Ok : StepState.Failed);
        return problems;
    }

    /// <summary>Version und Changelog schreiben (nicht beim Test-Feed), aufräumen, bauen, testen, veröffentlichen, packen. False bei Fehler oder Abbruch.</summary>
    public async Task<bool> BuildAsync(CancellationToken cancellation)
    {
        var step = ReleaseStep.Build;
        try
        {
            if (!IsLocalFeed)
            {
                var csproj = File.ReadAllText(Paths.AppProject);
                var current = ReleaseVersion.Current(csproj);
                _originalCsproj = csproj;
                File.WriteAllText(Paths.AppProject, ReleaseVersion.WithVersion(csproj, current, Version), new UTF8Encoding(false));
                var changelog = File.ReadAllText(Paths.Changelog);
                _originalChangelog = changelog;
                var released = Changelog.Release(changelog, _options.UnreleasedBlock, Version, _options.Today);
                File.WriteAllText(Paths.Changelog, released, new UTF8Encoding(false));
                Notes = Changelog.NotesFor(released, Version);
            }

            Set(step, StepState.Running);
            CleanBuildOutput();
            if (!await Dotnet(cancellation, "build", ReleasePaths.SolutionName, "-c", Configuration))
            {
                return Fail(step);
            }

            Set(step, StepState.Ok);
            step = ReleaseStep.Tests;
            if (IsLocalFeed)
            {
                Set(step, StepState.Skipped);
            }
            else
            {
                Set(step, StepState.Running);
                if (!await Dotnet(cancellation, "run", "--project", ChecksProject, "-c", Configuration, "--no-build"))
                {
                    return Fail(step);
                }

                Set(step, StepState.Ok);
            }

            step = ReleaseStep.Publish;
            Set(step, StepState.Running);
            if (Directory.Exists(Output))
            {
                Directory.Delete(Output, recursive: true);
            }

            if (!await Dotnet(
                    cancellation,
                    ["publish", AppProjectFolder, "-c", Configuration, "-r", "win-x64", "--self-contained", "true", .. SingleFileProperties, $"-p:Version={Version}", "-o", Paths.AppOutputFor(Version)]))
            {
                return Fail(step);
            }

            Set(step, StepState.Ok);
            step = ReleaseStep.Package;
            Set(step, StepState.Running);
            if (!File.Exists(Path.Combine(Paths.AppOutputFor(Version), UpdatePaths.ExecutableName)))
            {
                Write($"FEHLER: {UpdatePaths.ExecutableName} fehlt in {Paths.AppOutputFor(Version)}.");
                return Fail(step);
            }

            ReleasePackaging.CreateZip(Paths.AppOutputFor(Version), Zip);
            var manifest = UpdateManifest.Create(Zip, Version);
            File.WriteAllBytes(Manifest, manifest.ToJson());
            Write($"{Zip} ({manifest.Size / 1024 / 1024} MB, SHA-256 {manifest.Sha256})");
            Set(step, StepState.Ok);
            return true;
        }
        catch (OperationCanceledException)
        {
            Write("Abgebrochen.");
            return Fail(step);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Write("FEHLER: " + ex.Message);
            return Fail(step);
        }
    }

    /// <summary>Manifest signieren und prüfen; beim Test-Feed danach den Feed schreiben. Das Passwort wird nicht gespeichert.</summary>
    public SignResult Sign(string password)
    {
        Set(ReleaseStep.Sign, StepState.Running);
        var manifest = File.ReadAllBytes(Manifest);
        string signature;
        try
        {
            signature = ManifestSignature.Sign(manifest, File.ReadAllBytes(_options.KeyFile), password);
        }
        catch (CryptographicException)
        {
            Write("FEHLER: Falsches Passwort oder kaputte Schlüsseldatei.");
            Set(ReleaseStep.Sign, StepState.Waiting);
            return SignResult.WrongPassword;
        }

        File.WriteAllText(Signature, signature, Encoding.ASCII);
        if (!ManifestSignature.Verify(manifest, signature, _publicKey))
        {
            Write("FEHLER: Signatur passt nicht zum öffentlichen Schlüssel in UpdateKeys.");
            Set(ReleaseStep.Sign, StepState.Failed);
            return SignResult.VerifyFailed;
        }

        Write("Signatur gültig.");
        Files = new[] { Zip, Manifest, Signature }.Select(file => (file, new FileInfo(file).Length)).ToList();
        Set(ReleaseStep.Sign, StepState.Ok);
        if (_options.LocalFeedFolder is { } feed)
        {
            WriteLocalFeed(feed);
        }

        return SignResult.Ok;
    }

    /// <summary>Lokaler Commit, öffentlicher Commit, Branch public, Tag, Push, GitHub-Release. False beim ersten Fehler.</summary>
    public async Task<bool> PublishAsync(CancellationToken cancellation)
    {
        var step = ReleaseStep.Commit;
        try
        {
            Set(step, StepState.Running);
            if ((await Git(cancellation, "commit", "-m", $"chore(release): v{Version}", "--", AppProjectForGit, ReleasePaths.ChangelogRelative)).ExitCode != 0)
            {
                return Fail(step, revert: false);
            }

            _committed = true;
            Set(step, StepState.Ok);

            step = ReleaseStep.PublicCommit;
            Set(step, StepState.Running);
            var created = await PublicCommit.CreateAsync(_runner, Paths.Root, Version, Write, cancellation);
            if (created.Commit is not { } commit)
            {
                Write("FEHLER: " + created.Error);
                return Fail(step, revert: false);
            }

            _publicCommit = commit;
            Set(step, StepState.Ok);

            step = ReleaseStep.Push;
            Set(step, StepState.Running);
            string[][] pushSteps =
            [
                ["update-ref", "refs/heads/public", commit],
                ["tag", $"v{Version}", commit],
                ["push", "origin", "refs/heads/public:refs/heads/main"],
                ["push", "origin", $"refs/tags/v{Version}"],
            ];
            foreach (var arguments in pushSteps)
            {
                if ((await Git(cancellation, arguments)).ExitCode != 0)
                {
                    Write($"FEHLER: git {string.Join(' ', arguments)} fehlgeschlagen. Nachholen mit:");
                    foreach (var rest in pushSteps.SkipWhile(a => a != arguments))
                    {
                        Write("  git " + string.Join(' ', rest));
                    }

                    return Fail(step, revert: false);
                }
            }

            Set(step, StepState.Ok);
            return await ReleasePageAsync(cancellation);
        }
        catch (OperationCanceledException)
        {
            Write("Abgebrochen.");
            return Fail(step, revert: false);
        }
    }

    /// <summary>Nur <c>gh release create</c> erneut: Commit und Tag sind schon auf GitHub.</summary>
    public Task<bool> RetryReleasePageAsync(CancellationToken cancellation) => ReleasePageAsync(cancellation);

    /// <summary>Nimmt csproj und Changelog zurück, solange der lokale Commit nicht gelaufen ist; danach wirkungslos.</summary>
    public void Revert()
    {
        if (_committed)
        {
            return;
        }

        if (_originalCsproj is { } csproj)
        {
            File.WriteAllText(Paths.AppProject, csproj, new UTF8Encoding(false));
            _originalCsproj = null;
            Write("Versionsänderung in der csproj zurückgenommen.");
        }

        if (_originalChangelog is { } changelog)
        {
            File.WriteAllText(Paths.Changelog, changelog, new UTF8Encoding(false));
            _originalChangelog = null;
            Write("Changelog zurückgenommen.");
        }
    }

    private async Task<bool> ReleasePageAsync(CancellationToken cancellation)
    {
        Set(ReleaseStep.GitHubRelease, StepState.Running);
        var notesFile = Path.Combine(Path.GetTempPath(), $"rnw-release-notes-{Guid.NewGuid():N}.md");
        try
        {
            File.WriteAllText(notesFile, Notes, new UTF8Encoding(false));
            var arguments = new List<string> { "release", "create", $"v{Version}" };
            arguments.AddRange(Files.Select(file => file.Path));
            arguments.AddRange(["--repo", Repository, "--title", $"v{Version}", "--notes-file", notesFile]);
            if ((await Run(_options.GhPath ?? "gh", cancellation, [.. arguments])).ExitCode != 0)
            {
                Write($"FEHLER: gh release create fehlgeschlagen. Commit und Tag v{Version} sind schon auf GitHub. \"Release-Seite nachholen\" wiederholt nur diesen Schritt.");
                Set(ReleaseStep.GitHubRelease, StepState.Failed);
                return false;
            }

            Write($"Release v{Version} veröffentlicht.");
            Set(ReleaseStep.GitHubRelease, StepState.Ok);
            return true;
        }
        catch (OperationCanceledException)
        {
            Write("Abgebrochen.");
            Set(ReleaseStep.GitHubRelease, StepState.Failed);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(notesFile);
            }
            catch (IOException)
            {
                // Temp-Datei bleibt liegen, harmlos.
            }
        }
    }

    private void WriteLocalFeed(string feed)
    {
        Set(ReleaseStep.LocalFeed, StepState.Running);
        Directory.CreateDirectory(feed);

        // Nur die eigenen Feed-Dateien ersetzen: ein falsch gewählter Ordner verliert keine fremden Dateien.
        foreach (var file in Directory.EnumerateFiles(feed))
        {
            var name = Path.GetFileName(file);
            if ((name.StartsWith("RnwTileGenerator-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                || name is UpdatePreparer.ManifestName or UpdatePreparer.SignatureName or LocalFeedHandler.ReleaseFile)
            {
                File.Delete(file);
            }
        }

        foreach (var (path, _) in Files)
        {
            File.Copy(path, Path.Combine(feed, Path.GetFileName(path)));
        }

        ReleasePackaging.WriteLocalFeed(feed, Version);
        Write($"Lokaler Feed fertig: {feed} (Debug-Build, Programmordner: {Paths.AppOutputFor(Version)})");
        Set(ReleaseStep.LocalFeed, StepState.Ok);
    }

    /// <summary>bin/obj next to every project of the repo (RNW keeps its projects in the root and under tools\), never in <see cref="NotBuilt"/>.</summary>
    private void CleanBuildOutput()
    {
        var projects = Directory.EnumerateFiles(Paths.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(Paths.Root, file).Split(Path.DirectorySeparatorChar).Any(part => NotBuilt.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        foreach (var folder in projects.SelectMany(project => new[] { "bin", "obj" }.Select(name => Path.Combine(Path.GetDirectoryName(project)!, name))))
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private async Task<bool> Dotnet(CancellationToken cancellation, params string[] arguments) =>
        (await Run("dotnet", cancellation, arguments)).ExitCode == 0;

    private Task<CommandResult> Git(CancellationToken cancellation, params string[] arguments) => Run("git", cancellation, arguments);

    private Task<CommandResult> Run(string program, CancellationToken cancellation, params string[] arguments)
    {
        Write($"> {program} {string.Join(' ', arguments)}");
        return _runner.RunAsync(program, arguments, Paths.Root, null, Write, cancellation);
    }

    private bool Fail(ReleaseStep step, bool revert = true)
    {
        Set(step, StepState.Failed);
        if (revert)
        {
            Revert();
        }

        return false;
    }

    private void Set(ReleaseStep step, StepState state) => StepChanged?.Invoke(step, state);

    private void Write(string line) => Log?.Invoke(line);
}
