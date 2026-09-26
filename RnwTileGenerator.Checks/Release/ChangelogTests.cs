using RnwTileGenerator.Release;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class ChangelogTests
{
    private const string Older = "v0.3.0 (25.09.2026)\r\nNew\r\n- old feature\r\n";

    private const string Block = "Unreleased\r\nNew\r\n- a\r\nChanged\r\nFixed\r\n- b\r\n  more\r\n";

    private static readonly string File = Block + "\r\n" + Older;

    private static readonly AppVersion V040 = new(0, 4, 0);

    [Fact]
    public void UnreleasedBlockStopsAtTheFirstVersion()
    {
        var block = Changelog.UnreleasedBlock(File);

        Assert.Equal(Block, block);
        Assert.DoesNotContain("v0.3.0", block);
        Assert.Equal(Changelog.EmptyUnreleased, Changelog.UnreleasedBlock(Changelog.EmptyUnreleased));
    }

    [Fact]
    public void ProblemsForMissingHeadUnknownGroupEntryOutsideGroupAndEmpty()
    {
        Assert.Contains(Changelog.Problems("New\r\n- a\r\n", File, V040), p => p.Contains("Unreleased"));
        Assert.Contains(Changelog.Problems("Unreleased\r\nRemoved\r\n- a\r\n", File, V040), p => p.Contains("Removed"));
        Assert.Contains(Changelog.Problems("Unreleased\r\n- x\r\nNew\r\n- a\r\n", File, V040), p => p.Contains("außerhalb"));
        Assert.Contains(Changelog.Problems(Changelog.EmptyUnreleased, File, V040), p => p.Contains("keinen Eintrag"));
        Assert.Empty(Changelog.Problems(Block, File, V040));
    }

    [Fact]
    public void ProblemWhenTheVersionAlreadyExists()
    {
        var file = Block + "\r\nv0.4.0 (01.01.2026)\r\nFixed\r\n- c\r\n";

        Assert.Contains(Changelog.Problems(Block, file, V040), p => p.Contains("0.4.0"));
    }

    [Fact]
    public void ReleaseTurnsUnreleasedIntoTheVersionAndDropsEmptyGroups()
    {
        var released = Changelog.Release(File, Block, V040, new DateOnly(2026, 9, 27));

        Assert.Equal(
            Changelog.EmptyUnreleased + "\r\nv0.4.0 (27.09.2026)\r\nNew\r\n- a\r\nFixed\r\n- b\r\n  more\r\n\r\n" + Older,
            released);
    }

    [Fact]
    public void ReleaseNormalizesLineEndingsAndTrailingSpace()
    {
        var messy = "Unreleased\nNew  \r\n- a \t\n\n\n- c\r\nChanged\nFixed\t\n- b\n";

        var released = Changelog.Release(File, messy, V040, new DateOnly(2026, 9, 27));

        Assert.DoesNotMatch("(?<!\r)\n", released);
        Assert.DoesNotMatch("[ \t]\r\n", released);
        Assert.Contains("v0.4.0 (27.09.2026)\r\nNew\r\n- a\r\n- c\r\nFixed\r\n- b\r\n\r\nv0.3.0", released);
    }

    [Fact]
    public void NotesForReturnsOnlyThatVersion()
    {
        var released = Changelog.Release(File, Block, V040, new DateOnly(2026, 9, 27));

        Assert.Equal("New\r\n- a\r\nFixed\r\n- b\r\n  more\r\n", Changelog.NotesFor(released, V040));
        Assert.Equal("New\r\n- old feature\r\n", Changelog.NotesFor(released, new AppVersion(0, 3, 0)));
        Assert.Equal(string.Empty, Changelog.NotesFor(released, new AppVersion(9, 9, 9)));
    }
}
