using RnwTileGenerator.Release;
using System.Text;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public sealed class ReleaseRunTests : IDisposable
{
    private const string Csproj = "<Project>\r\n  <PropertyGroup>\r\n    <Version>0.3.0</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n";

    private const string ChangelogText = "Unreleased\r\nNew\r\n- a\r\nChanged\r\nFixed\r\n- \"Quote\" äöü 100%\r\n\r\nv0.3.0 (25.09.2026)\r\nNew\r\n- old\r\n";

    private static readonly AppVersion V040 = new(0, 4, 0);

    internal const string AppInfoText = "public const string BugReportEmail = \"bugs@example.com\";\r\n";

    private readonly string _root = Directory.CreateTempSubdirectory("rnw-release-run-").FullName;

    private readonly FakeRunner _runner = new();

    private readonly (string PublicKey, byte[] PrivateKey) _keys = ManifestSignature.CreateKeyPair("pw");

    public ReleaseRunTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.AppProject)!);
        File.WriteAllText(Paths.AppProject, Csproj);
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Changelog)!);
        File.WriteAllText(Paths.Changelog, ChangelogText);
        File.WriteAllBytes(KeyFile, _keys.PrivateKey);
        File.WriteAllText(Paths.AppInfo, AppInfoText);
        _runner.Respond = (program, args) => (program, args.FirstOrDefault()) switch
        {
            ("git", "branch") => new CommandResult(0, ["master"]),
            ("git", "write-tree") => new CommandResult(0, ["tree1"]),
            ("git", "rev-parse") => new CommandResult(1, []),
            ("git", "commit-tree") => new CommandResult(0, ["commit1"]),
            ("git", "-c") => new CommandResult(0, ["a.txt"]),
            ("git", "log") => new CommandResult(0, [$"{PublicCommit.Email}|{PublicCommit.Email}"]),
            _ => null,
        };
    }

    private ReleasePaths Paths => new(_root);

    private string KeyFile => Path.Combine(_root, "release-key.p8");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ReleaseRun Run(string? localFeed = null, string? gh = @"C:\gh\gh.exe", string? keyFile = null) =>
        new(new ReleaseOptions(Paths, V040, Changelog.UnreleasedBlock(ChangelogText), localFeed, new DateOnly(2026, 9, 27), keyFile ?? KeyFile, gh), _runner, _keys.PublicKey);

    [Fact]
    public async Task ChecksReportMissingKeyWrongBranchDirtyTreeAndGh()
    {
        _runner.Respond = (program, args) => (program, args.FirstOrDefault()) switch
        {
            ("git", "branch") => new CommandResult(0, ["feature"]),
            ("git", "status") => new CommandResult(0, [" M src/x.cs", " M Readme/Changelog.txt"]),
            ("git", "remote") => new CommandResult(2, []),
            (_, "auth") => new CommandResult(1, []),
            _ => null,
        };

        var problems = await Run(keyFile: Path.Combine(_root, "fehlt.p8")).CheckAsync(CancellationToken.None);

        Assert.Contains(problems, p => p.Contains("master"));
        Assert.Contains(problems, p => p.Contains("src/x.cs"));
        Assert.DoesNotContain(problems, p => p.Contains("Readme/Changelog.txt"));
        Assert.Contains(problems, p => p.Contains("origin"));
        Assert.Contains(problems, p => p.Contains("fehlt.p8"));
        Assert.Contains(problems, p => p.Contains("auth login"));
        Assert.Contains(await Run(gh: null).CheckAsync(CancellationToken.None), p => p.Contains("gh"));
    }

    [Fact]
    public async Task ChecksForTheLocalFeedCallNeitherGitNorGh()
    {
        var problems = await Run(localFeed: Path.Combine(_root, "feed"), gh: null).CheckAsync(CancellationToken.None);

        Assert.Empty(problems);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task BuildRunsTheCommandsInOrder()
    {
        Assert.True(await Run().BuildAsync(CancellationToken.None));

        Assert.Equal(
            new[]
            {
                "dotnet build RnwTileGenerator.sln -c Release",
                "dotnet run --project RnwTileGenerator.Checks -c Release --no-build",
                $"dotnet publish RnwTileGenerator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:Version=0.4.0 -o {Paths.AppOutputFor(V040)}",
            },
            _runner.Calls);
        Assert.Contains("<Version>0.4.0</Version>", File.ReadAllText(Paths.AppProject));
        Assert.StartsWith(Changelog.EmptyUnreleased + "\r\nv0.4.0 (27.09.2026)", File.ReadAllText(Paths.Changelog));
        Assert.True(File.Exists(Path.Combine(Paths.OutputFor(V040), ReleasePackaging.ZipName(V040))));
        Assert.True(File.Exists(Path.Combine(Paths.OutputFor(V040), UpdatePreparer.ManifestName)));
    }

    [Fact]
    public async Task LocalFeedBuildsDebugWithoutTestsAndLeavesCsprojAndChangelog()
    {
        Assert.True(await Run(localFeed: Path.Combine(_root, "feed")).BuildAsync(CancellationToken.None));

        Assert.Equal(2, _runner.Calls.Count);
        Assert.StartsWith("dotnet build RnwTileGenerator.sln -c Debug", _runner.Calls[0]);
        Assert.StartsWith("dotnet publish RnwTileGenerator.App -c Debug", _runner.Calls[1]);
        Assert.Equal(Csproj, File.ReadAllText(Paths.AppProject));
        Assert.Equal(ChangelogText, File.ReadAllText(Paths.Changelog));
    }

    [Fact]
    public async Task BuildFailureRevertsCsprojAndChangelog()
    {
        _runner.Respond = (program, args) => program == "dotnet" && args[0] == "build" ? new CommandResult(1, []) : null;
        var run = Run();
        var failed = new List<ReleaseStep>();
        run.StepChanged += (step, state) =>
        {
            if (state == StepState.Failed)
            {
                failed.Add(step);
            }
        };

        Assert.False(await run.BuildAsync(CancellationToken.None));

        Assert.Equal(new[] { ReleaseStep.Build }, failed);
        Assert.Equal(Csproj, File.ReadAllText(Paths.AppProject));
        Assert.Equal(ChangelogText, File.ReadAllText(Paths.Changelog));
    }

    [Fact]
    public async Task CancelDuringBuildRevertsBoth()
    {
        _runner.Respond = (program, args) => program == "dotnet" && args[0] == "run" ? throw new OperationCanceledException() : null;

        Assert.False(await Run().BuildAsync(CancellationToken.None));

        Assert.Equal(Csproj, File.ReadAllText(Paths.AppProject));
        Assert.Equal(ChangelogText, File.ReadAllText(Paths.Changelog));
    }

    [Fact]
    public async Task SignWithWrongPasswordKeepsTheStepOpen()
    {
        var run = Run();
        await run.BuildAsync(CancellationToken.None);

        Assert.Equal(SignResult.WrongPassword, run.Sign("falsch"));
        Assert.Equal(SignResult.Ok, run.Sign("pw"));

        Assert.True(File.Exists(Path.Combine(Paths.OutputFor(V040), UpdatePreparer.SignatureName)));
        Assert.Equal(3, run.Files.Count);
    }

    [Fact]
    public async Task LocalFeedIsWrittenAfterSigning()
    {
        var feed = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
        File.WriteAllText(Path.Combine(feed, "RnwTileGenerator-0.1.0-win-x64.zip"), "alt");
        File.WriteAllText(Path.Combine(feed, "fremd.txt"), "bleibt");
        var run = Run(localFeed: feed);
        await run.BuildAsync(CancellationToken.None);

        Assert.Equal(SignResult.Ok, run.Sign("pw"));

        Assert.False(File.Exists(Path.Combine(feed, "RnwTileGenerator-0.1.0-win-x64.zip")));
        Assert.True(File.Exists(Path.Combine(feed, "fremd.txt")));
        Assert.True(File.Exists(Path.Combine(feed, ReleasePackaging.ZipName(V040))));
        Assert.True(File.Exists(Path.Combine(feed, LocalFeedHandler.ReleaseFile)));
    }

    private async Task<ReleaseRun> SignedRun()
    {
        var run = Run();
        Assert.True(await run.BuildAsync(CancellationToken.None));
        Assert.Equal(SignResult.Ok, run.Sign("pw"));
        _runner.Calls.Clear();
        return run;
    }

    [Fact]
    public async Task PublishRunsCommitPublicCommitTagPushAndRelease()
    {
        var run = await SignedRun();
        var changelogAfterBuild = File.ReadAllText(Paths.Changelog);

        Assert.True(await run.PublishAsync(CancellationToken.None));

        var calls = _runner.Calls;
        Assert.Equal("git commit -m chore(release): v0.4.0 -- RnwTileGenerator.App/RnwTileGenerator.App.csproj Readme/Changelog.txt", calls[0]);
        Assert.Equal("git read-tree HEAD", calls[1]);
        Assert.Contains("git update-ref refs/heads/public commit1", calls);
        Assert.Contains("git tag v0.4.0 commit1", calls);
        var pushMain = calls.IndexOf("git push origin refs/heads/public:refs/heads/main");
        var pushTag = calls.IndexOf("git push origin refs/tags/v0.4.0");
        var release = calls.FindIndex(c => c.StartsWith(@"C:\gh\gh.exe release create v0.4.0 ", StringComparison.Ordinal));
        Assert.True(pushMain > 0 && pushTag > pushMain && release > pushTag);
        Assert.Contains("--repo flogi1/RNW-Tile-Generator-for-EU-IV --title v0.4.0 --notes-file ", calls[release]);
        foreach (var file in run.Files)
        {
            Assert.Contains(file.Path, calls[release]);
        }

        run.Revert();
        Assert.Contains("<Version>0.4.0</Version>", File.ReadAllText(Paths.AppProject));
        Assert.Equal(changelogAfterBuild, File.ReadAllText(Paths.Changelog));
    }

    [Fact]
    public async Task NoPushWhenThePublicCommitCheckFails()
    {
        var run = await SignedRun();
        var earlier = _runner.Respond;
        _runner.Respond = (program, args) => program == "git" && args[0] == "-c" ? new CommandResult(0, ["a.txt", "docs/x.md"]) : earlier(program, args);
        var failed = new List<ReleaseStep>();
        run.StepChanged += (step, state) =>
        {
            if (state == StepState.Failed)
            {
                failed.Add(step);
            }
        };

        Assert.False(await run.PublishAsync(CancellationToken.None));

        Assert.DoesNotContain(_runner.Calls, c => c.StartsWith("git push", StringComparison.Ordinal));
        Assert.Equal(new[] { ReleaseStep.PublicCommit }, failed);
    }

    [Fact]
    public async Task GhFailureKeepsPushesAndRetryRepeatsOnlyGh()
    {
        var run = await SignedRun();
        var earlier = _runner.Respond;
        var ghCalls = 0;
        _runner.Respond = (program, args) => program.EndsWith("gh.exe", StringComparison.Ordinal) && ++ghCalls == 1 ? new CommandResult(1, []) : earlier(program, args);

        Assert.False(await run.PublishAsync(CancellationToken.None));
        Assert.Contains("git push origin refs/tags/v0.4.0", _runner.Calls);
        _runner.Calls.Clear();

        Assert.True(await run.RetryReleasePageAsync(CancellationToken.None));

        Assert.Single(_runner.Calls);
        Assert.StartsWith(@"C:\gh\gh.exe release create v0.4.0 ", _runner.Calls[0]);
    }

    [Fact]
    public async Task NotesGoToGhAsAUtf8File()
    {
        var run = await SignedRun();
        var earlier = _runner.Respond;
        byte[]? notes = null;
        _runner.Respond = (program, args) =>
        {
            if (program.EndsWith("gh.exe", StringComparison.Ordinal))
            {
                notes = File.ReadAllBytes(args[args.ToList().IndexOf("--notes-file") + 1]);
            }

            return earlier(program, args);
        };

        Assert.True(await run.PublishAsync(CancellationToken.None));

        Assert.Equal(Encoding.UTF8.GetBytes("New\r\n- a\r\nFixed\r\n- \"Quote\" äöü 100%\r\n"), notes);
        Assert.Equal("New\r\n- a\r\nFixed\r\n- \"Quote\" äöü 100%\r\n", run.Notes);
    }

    /// <summary>Zeichnet Aufrufe als "programm arg1 arg2" auf; <see cref="Respond"/> null = Exitcode 0 ohne Ausgabe. Beim Publish legt sie die exe an.</summary>
    private sealed class FakeRunner : ICommandRunner
    {
        public List<string> Calls { get; } = new();

        public Func<string, IReadOnlyList<string>, CommandResult?> Respond { get; set; } = (_, _) => null;

        public Task<CommandResult> RunAsync(
            string program,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            IReadOnlyDictionary<string, string>? environment,
            Action<string> log,
            CancellationToken cancellation)
        {
            Calls.Add(string.Join(' ', new[] { program }.Concat(arguments)));
            if (program == "dotnet" && arguments[0] == "publish")
            {
                var output = arguments[arguments.ToList().IndexOf("-o") + 1];
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, UpdatePaths.ExecutableName), "exe");
            }

            return Task.FromResult(Respond(program, arguments) ?? new CommandResult(0, []));
        }
    }
}
