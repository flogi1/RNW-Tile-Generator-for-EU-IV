using RnwTileGenerator.App;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class AppChecks : IDisposable
{
    /// <summary>Key prefixes added by the auto-update work; every one of their texts must exist in all 7 languages.</summary>
    private static readonly string[] Prefixes = ["update.", "help.", "bug.", "crash.", "save."];

    public void Dispose() => Loc.UseLanguage(AppLanguage.English);

    private static List<KeyValuePair<string, IReadOnlyDictionary<AppLanguage, string>>> NewEntries() =>
        Loc.Entries.Where(entry => Prefixes.Any(prefix => entry.Key.StartsWith(prefix, StringComparison.Ordinal))).ToList();

    [Fact]
    public void All_new_texts_have_seven_languages()
    {
        var entries = NewEntries();
        foreach (var prefix in Prefixes)
            Assert.True(entries.Any(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal)), "no entries for " + prefix);

        var missing = entries
            .SelectMany(entry => Loc.AllLanguages.Where(language => !entry.Value.TryGetValue(language, out var text) || string.IsNullOrWhiteSpace(text))
                .Select(language => $"{entry.Key} ({language})"))
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void No_label_ends_with_ellipsis()
    {
        var withEllipsis = NewEntries()
            .SelectMany(entry => entry.Value.Where(pair => pair.Value.TrimEnd().EndsWith("...", StringComparison.Ordinal) || pair.Value.TrimEnd().EndsWith('…'))
                .Select(pair => $"{entry.Key} ({pair.Key})"))
            .ToList();
        Assert.Empty(withEllipsis);
    }

    [Fact]
    public void Format_texts_accept_their_arguments()
    {
        foreach (var language in Loc.AllLanguages)
        {
            Loc.UseLanguage(language);
            var bar = UpdateTexts.BarAvailable(new AppVersion(1, 2, 0), new AppVersion(1, 1, 0));
            Assert.Contains("1.2.0", bar);
            Assert.Contains("1.1.0", bar);
            Assert.Contains("C:\\x", UpdateTexts.ProtectedFolder("C:\\x"));
            Assert.Contains("7", UpdateTexts.Phase(UpdateRunnerPhase.Waiting, 7));
            Assert.Contains("boom", UpdateTexts.Problem(UpdateProblem.Network, "boom"));
            Assert.Contains("log.txt", UpdateTexts.RollbackFailed("backup", "log.txt"));
        }
    }

    [Fact]
    public void Every_update_problem_has_a_text()
    {
        foreach (var problem in Enum.GetValues<UpdateProblem>().Where(p => p != UpdateProblem.None))
            Assert.False(string.IsNullOrWhiteSpace(UpdateTexts.Problem(problem, "x")), problem.ToString());
    }

    [Fact]
    public void Language_of_the_program_folder_is_used_without_saving()
    {
        using var temp = new TempFolder();
        temp.WriteFile("language.json", "{ \"language\": \"German\" }");
        Loc.UseLanguageOf(temp.Path);
        Assert.Equal(AppLanguage.German, Loc.Current);
        Assert.Equal(1, Directory.GetFiles(temp.Path).Length);
    }

    [Fact]
    public void Donate_links_are_https_addresses()
    {
        foreach (var url in new[] { AppInfo.GitHubSponsorsUrl, AppInfo.KofiUrl })
            Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, url);
        Assert.Equal("https://ko-fi.com/flogi", AppInfo.KofiUrl);
        Assert.Contains(Loc.Entries, entry => entry.Key == "menu.donate.kofi");
        Assert.Contains(Loc.Entries, entry => entry.Key == "menu.donate.github");
    }

    [Fact]
    public void App_version_comes_from_the_assembly()
    {
        Assert.True(AppInfo.Version > new AppVersion(1, 0, 0));
        Assert.Equal("flogi1/RNW-Tile-Generator-for-EU-IV", AppInfo.Repository);
    }
}
