using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class ReadmeFilesTests
{
    [Fact]
    public void ChangelogLivesInTheReadmeFolder()
    {
        Assert.Equal(@"C:\RNW\Readme\Changelog.txt", ReadmeFiles.ChangelogIn(@"C:\RNW"));
    }
}
