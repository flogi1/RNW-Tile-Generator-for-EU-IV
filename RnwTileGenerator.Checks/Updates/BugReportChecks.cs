using RnwTileGenerator.Updates;

namespace RnwTileGenerator.Checks;

public class BugReportChecks
{
    private const string Repo = "flogi1/RNW-Tile-Generator-for-EU-IV";

    private static BugReportInput Input(string title = "Export crashes", string what = "It crashed.", string? log = null) =>
        new(title, what, "1. Open tile\n2. Export", "Expected a file, got a crash", log, "1.1.0", "Microsoft Windows 10.0.26200", ".NET 10.0.1", "Deutsch");

    [Fact]
    public void Text_contains_environment()
    {
        var text = BugReport.BuildText(Input());
        Assert.Contains("## What happened?", text);
        Assert.Contains("## Steps to reproduce", text);
        Assert.Contains("## Expected / actual", text);
        Assert.Contains("- RNW Tile Generator: 1.1.0", text);
        Assert.Contains("- Windows: Microsoft Windows 10.0.26200", text);
        Assert.Contains("- Runtime: .NET 10.0.1", text);
        Assert.Contains("- UI language: Deutsch", text);
        Assert.DoesNotContain("## Crash log", text);
    }

    [Fact]
    public void Crash_log_section_only_when_present()
    {
        var text = BugReport.BuildText(Input(log: "System.Exception: boom"));
        Assert.Contains("## Crash log", text);
        Assert.Contains("```\r\nSystem.Exception: boom\r\n```", text);
    }

    [Fact]
    public void GitHub_url_has_label_and_title()
    {
        var link = BugReport.GitHubUrl(Repo, Input());
        Assert.StartsWith("https://github.com/flogi1/RNW-Tile-Generator-for-EU-IV/issues/new?title=Export%20crashes&body=", link.Url);
        Assert.EndsWith("&labels=bug", link.Url);
        Assert.DoesNotContain(" ", link.Url);
        Assert.False(link.Truncated);
    }

    [Fact]
    public void Mailto_has_subject_prefix_and_crlf_body()
    {
        var link = BugReport.MailtoUrl("bugs@example.com", Input());
        Assert.StartsWith("mailto:bugs@example.com?subject=%5BRNW%20Bug%5D%20Export%20crashes&body=", link.Url);
        Assert.Contains("%0D%0A", link.Url);
        Assert.False(link.Truncated);
    }

    [Fact]
    public void Cyrillic_and_long_log_stay_under_limit()
    {
        var input = Input(title: "Ошибка при экспорте", what: new string('错', 3000), log: string.Concat(Enumerable.Repeat("[2026-09-26 10:00:00] boom at X\n", 700)));
        foreach (var link in new[] { BugReport.GitHubUrl(Repo, input), BugReport.MailtoUrl("bugs@example.com", input) })
        {
            Assert.True(link.Url.Length <= BugReport.MaxUrlLength, $"length {link.Url.Length}");
            Assert.True(link.Truncated);
            Assert.Contains(Uri.EscapeDataString("Ошибка при экспорте"), link.Url);
            Assert.Contains(Uri.EscapeDataString(BugReport.TruncatedNote), link.Url);
        }
    }

    [Fact]
    public void Crash_log_is_cut_before_the_free_texts()
    {
        var input = Input(what: "Short description stays complete.", log: new string('x', 20000));
        var link = BugReport.GitHubUrl(Repo, input);
        Assert.True(link.Truncated);
        Assert.Contains(Uri.EscapeDataString("Short description stays complete."), link.Url);
    }

    [Fact]
    public void Short_report_is_not_truncated()
    {
        Assert.False(BugReport.GitHubUrl(Repo, Input(log: "small log")).Truncated);
        Assert.False(BugReport.MailtoUrl("bugs@example.com", Input(log: "small log")).Truncated);
    }

    [Fact]
    public void Anonymize_replaces_profile_and_paths()
    {
        Assert.Equal(@"%USERPROFILE%\Documents\x.rnwproj", BugReport.Anonymize(@"C:\Users\Joerg\Documents\x.rnwproj", @"C:\Users\joerg"));
        Assert.Equal(@"Could not read …\tile.bmp now", BugReport.Anonymize(@"Could not read D:\Games\EU4\map\tile.bmp now", @"C:\Users\joerg"));
        Assert.Equal("no paths here", BugReport.Anonymize("no paths here", @"C:\Users\joerg"));
    }

    [Fact]
    public void Anonymize_drops_folder_names_with_spaces()
    {
        Assert.Equal(@"read …\t.bmp failed", BugReport.Anonymize(@"read D:\Spiele\Jörg Müller Mods\t.bmp failed", @"C:\Users\joerg"));
        Assert.Equal(@"at X() in …\x.cs:line 5", BugReport.Anonymize(@"at X() in C:\Program Files\RNW Tile Generator\x.cs:line 5", @"C:\Users\joerg"));
        Assert.DoesNotContain("Müller", BugReport.Anonymize("\"D:\\Spiele\\Jörg Müller Mods\\a b.rnwproj\"", @"C:\Users\joerg"));
    }

    [Fact]
    public void Last_crash_entries_start_at_entry_header()
    {
        var log = "[2026-09-01 10:00:00] first\nold stack\n[2026-09-02 10:00:00] second\nnew stack\n";
        Assert.Equal(log, BugReport.LastCrashEntries(log, 1000));
        var tail = BugReport.LastCrashEntries(log, 40);
        Assert.StartsWith("[2026-09-02", tail);
        Assert.EndsWith("new stack\n", tail);
    }
}
