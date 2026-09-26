using RnwTileGenerator.Release;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

/// <summary>RNW-specific release rules on top of the checks ported from PMT.</summary>
public sealed class RnwReleaseChecks : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("rnw-release-rnw-").FullName;
    private readonly ProcessCommandRunner _runner = new();

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<string> Git(params string[] arguments)
    {
        var result = _runner.RunAsync("git", arguments, _root, null, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} → {result.ExitCode}");
        return result.Output;
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public async Task Public_commit_excludes_all_private_paths()
    {
        Git("init", "-q", "-b", "master");
        Git("config", "user.name", "Privat");
        Git("config", "user.email", "privat@example.com");
        Write("docs/a.md", "x");
        Write("refs/tweaks.lua", "x");
        Write("Migration Files/m.txt", "x");
        Write("loco/l.cs", "x");
        Write("CLAUDE.md", "x");
        Write("Readme.txt", "x");
        Write("src/a.cs", "x");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
        var status = Git("status", "--porcelain");

        var result = await PublicCommit.CreateAsync(_runner, _root, new AppVersion(1, 1, 0), _ => { }, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "Readme.txt", "src/a.cs" }, Git("-c", "core.quotepath=off", "ls-tree", "-r", "--name-only", result.Commit!));
        Assert.Equal($"{PublicCommit.Email}|{PublicCommit.Email}", Git("log", "-1", "--format=%ae|%ce", result.Commit!)[0]);
        Assert.Equal(status, Git("status", "--porcelain"));
        Assert.Equal(7, Git("ls-files").Count);
    }

    [Theory]
    [InlineData("CLAUDE.md", true)]
    [InlineData("docs/x.md", true)]
    [InlineData("Migration Files/CLAUDE.md", true)]
    [InlineData("refs", true)]
    [InlineData("CLAUDE.md.bak", false)]
    [InlineData("documentation/x.md", false)]
    [InlineData("RnwTileGenerator.App/refs.cs", false)]
    public void Excluded_path_matching(string path, bool excluded) => Assert.Equal(excluded, PublicCommit.IsExcluded(path));

    private ReleaseRun Run(string appInfo, string? localFeed)
    {
        var paths = new ReleasePaths(_root);
        Write(ReleasePaths.AppProjectRelative, "<Project><PropertyGroup><Version>1.0.5</Version></PropertyGroup></Project>");
        Write(ReleasePaths.AppInfoRelative, appInfo);
        Write("Readme/Changelog.txt", "Unreleased\r\nNew\r\n- a\r\nChanged\r\nFixed\r\n");
        Write("release-key.p8", "key");
        var options = new ReleaseOptions(paths, new AppVersion(1, 1, 0), "Unreleased\r\nNew\r\n- a\r\nChanged\r\nFixed\r\n", localFeed,
            new DateOnly(2026, 9, 27), Path.Combine(_root, "release-key.p8"), @"C:\gh\gh.exe");
        return new ReleaseRun(options, new NoopRunner());
    }

    [Fact]
    public async Task Placeholder_in_app_info_blocks_real_release()
    {
        var problems = await Run("const string BugReportEmail = \"PLACEHOLDER@example.com\";", localFeed: null).CheckAsync(CancellationToken.None);
        Assert.Contains(problems, p => p.Contains("PLACEHOLDER"));
    }

    [Fact]
    public async Task Placeholder_only_warns_in_local_feed()
    {
        var log = new List<string>();
        var run = Run("const string BugReportEmail = \"PLACEHOLDER@example.com\";", localFeed: Path.Combine(_root, "feed"));
        run.Log += log.Add;
        Assert.Empty(await run.CheckAsync(CancellationToken.None));
        Assert.Contains(log, line => line.Contains("PLACEHOLDER"));
    }

    /// <summary>The repo's real AppInfo.cs with both values filled in must pass (a comment mentioning the marker once blocked every release).</summary>
    [Fact]
    public async Task Real_app_info_with_filled_values_passes()
    {
        var repo = ReleasePaths.FindRoot(AppContext.BaseDirectory);
        Assert.NotNull(repo);
        var real = File.ReadAllText(Path.Combine(repo, ReleasePaths.AppInfoRelative));
        var filled = real.Replace("PLACEHOLDER@example.com", "bugs@example.com").Replace("sponsors/PLACEHOLDER", "sponsors/someone");

        var problems = await Run(filled, localFeed: null).CheckAsync(CancellationToken.None);

        Assert.DoesNotContain(problems, p => p.Contains("PLACEHOLDER"));
    }

    [Fact]
    public void Tool_is_stale_when_the_update_library_changed()
    {
        var t = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        Write("RnwTileGenerator.Updates/UpdateKeys.cs", "class K {}");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "RnwTileGenerator.Updates", "UpdateKeys.cs"), t);

        Assert.True(new ReleasePaths(_root).ToolSourcesNewerThan(t.AddMinutes(-1)));
        Assert.False(new ReleasePaths(_root).ToolSourcesNewerThan(t.AddMinutes(1)));
    }

    [Fact]
    public async Task Filled_app_info_passes()
    {
        var problems = await Run("const string BugReportEmail = \"bugs@example.com\";", localFeed: null).CheckAsync(CancellationToken.None);
        Assert.DoesNotContain(problems, p => p.Contains("PLACEHOLDER"));
    }

    /// <summary>Every command succeeds; git answers "master" to branch and nothing dirty to status.</summary>
    private sealed class NoopRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string workingDirectory,
            IReadOnlyDictionary<string, string>? environment, Action<string> log, CancellationToken cancellation) =>
            Task.FromResult(arguments.FirstOrDefault() == "branch" ? new CommandResult(0, ["master"]) : new CommandResult(0, []));
    }
}
