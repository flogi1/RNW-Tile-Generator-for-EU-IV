using RnwTileGenerator.Release;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

/// <summary>Gegen ein echtes Git-Repo im Temp-Ordner (git muss installiert sein, wie für jeden Release).</summary>
public sealed class PublicCommitTests : IDisposable
{
    private static readonly AppVersion V040 = new(0, 4, 0);

    private readonly string _repo = Directory.CreateTempSubdirectory("rnw-public-commit-").FullName;

    private readonly ProcessCommandRunner _runner = new();

    public PublicCommitTests()
    {
        Git("init", "-q", "-b", "master");
        Git("config", "user.name", "Privat");
        Git("config", "user.email", "privat@example.com");
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        File.WriteAllText(Path.Combine(_repo, "docs", "spec.md"), "geheim");
        File.WriteAllText(Path.Combine(_repo, "docs", "Übersicht.md"), "geheim");
        Git("add", "-A");
        Git("commit", "-q", "-m", "init");
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private IReadOnlyList<string> Git(params string[] arguments)
    {
        var result = _runner.RunAsync("git", arguments, _repo, null, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} → {result.ExitCode}");
        return result.Output;
    }

    private Task<PublicCommitResult> Create() => PublicCommit.CreateAsync(_runner, _repo, V040, _ => { }, CancellationToken.None);

    [Fact]
    public async Task FirstPublicCommitHasNoParent()
    {
        var result = await Create();

        Assert.Null(result.Error);
        Assert.Single(Git("rev-list", "--parents", "-n", "1", result.Commit!)[0].Split(' '));
    }

    [Fact]
    public async Task PublicCommitLeavesDocsOutAndUsesThePublicAddress()
    {
        var commit = (await Create()).Commit!;

        var files = Git("-c", "core.quotepath=off", "ls-tree", "-r", "--name-only", commit);
        Assert.Equal(new[] { "a.txt" }, files);
        Assert.Equal("Florian <152910095+flogi1@users.noreply.github.com>", Git("log", "-1", "--format=%an <%ae>", commit)[0]);
        Assert.Equal("Florian <152910095+flogi1@users.noreply.github.com>", Git("log", "-1", "--format=%cn <%ce>", commit)[0]);
        Assert.Equal("Release v0.4.0", Git("log", "-1", "--format=%s", commit)[0]);
    }

    [Fact]
    public async Task SecondPublicCommitHasThePreviousAsParent()
    {
        var first = (await Create()).Commit!;
        Git("update-ref", "refs/heads/public", first);

        var second = (await Create()).Commit!;

        Assert.Equal(new[] { second, first }, Git("rev-list", "--parents", "-n", "1", second)[0].Split(' '));
    }

    [Fact]
    public async Task WorkingTreeAndIndexStayUntouched()
    {
        var status = Git("status", "--porcelain");

        await Create();

        Assert.Equal(status, Git("status", "--porcelain"));
        Assert.Equal(2, Git("ls-files", "docs").Count);
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), "rnw-public-index-*"));
    }
}
