using System.Text;
using System.Text.RegularExpressions;

namespace RnwTileGenerator.Updates;

/// <summary>What the user typed into the bug report window plus the environment lines added automatically.</summary>
public sealed record BugReportInput(
    string Title,
    string WhatHappened,
    string Steps,
    string ExpectedActual,
    string? CrashLog,
    string AppVersion,
    string OsDescription,
    string Runtime,
    string UiLanguage);

/// <summary>A prefilled GitHub-issue or mailto address; <see cref="Truncated"/> = texts were shortened to fit.</summary>
public sealed record BugReportLink(string Url, bool Truncated);

/// <summary>
/// Builds the bug report text (always English headings) and the prefilled GitHub-issue and mailto addresses.
/// Browsers and mail programs reject very long addresses, so both stay within <see cref="MaxUrlLength"/>:
/// the crash log is shortened first (its end is kept), then the longest free text (its start is kept), each
/// marked with <see cref="TruncatedNote"/>. The title is shortened only as a last resort.
/// </summary>
public static partial class BugReport
{
    public const int MaxUrlLength = 8000;

    public const int MaxCrashLogChars = 3000;

    public const string TruncatedNote = "(truncated, full text copied to clipboard)";

    private const string NewLine = "\r\n";

    public static string BuildText(BugReportInput input)
    {
        var text = new StringBuilder()
            .Append("## What happened?").Append(NewLine).Append(Normalize(input.WhatHappened)).Append(NewLine).Append(NewLine)
            .Append("## Steps to reproduce").Append(NewLine).Append(Normalize(input.Steps)).Append(NewLine).Append(NewLine)
            .Append("## Expected / actual").Append(NewLine).Append(Normalize(input.ExpectedActual)).Append(NewLine).Append(NewLine);
        if (!string.IsNullOrWhiteSpace(input.CrashLog))
        {
            text.Append("## Crash log").Append(NewLine)
                .Append("```").Append(NewLine).Append(Normalize(input.CrashLog).TrimEnd()).Append(NewLine).Append("```").Append(NewLine).Append(NewLine);
        }

        return text
            .Append("## Environment").Append(NewLine)
            .Append("- RNW Tile Generator: ").Append(input.AppVersion).Append(NewLine)
            .Append("- Windows: ").Append(input.OsDescription).Append(NewLine)
            .Append("- Runtime: ").Append(input.Runtime).Append(NewLine)
            .Append("- UI language: ").Append(input.UiLanguage).Append(NewLine)
            .ToString();
    }

    public static BugReportLink GitHubUrl(string repository, BugReportInput input) =>
        Fit(input, current => $"https://github.com/{repository}/issues/new?title={E(current.Title)}&body={E(BuildText(current))}&labels=bug");

    public static BugReportLink MailtoUrl(string address, BugReportInput input) =>
        Fit(input, current => $"mailto:{address}?subject={E("[RNW Bug] " + current.Title)}&body={E(BuildText(current))}");

    /// <summary>Replaces the user profile folder by %USERPROFILE%, then replaces the folder part of every other absolute drive path by "…\" (the file name stays).</summary>
    public static string Anonymize(string text, string userProfile)
    {
        if (!string.IsNullOrEmpty(userProfile))
        {
            text = Regex.Replace(text, Regex.Escape(Path.TrimEndingDirectorySeparator(userProfile)), "%USERPROFILE%", RegexOptions.IgnoreCase);
        }

        // Only the folder part of a drive path goes (folder names may contain spaces); the file name stays for context.
        return DriveFolders().Replace(text, "…\\");
    }

    /// <summary>At most <paramref name="maxChars"/> from the end of the log, starting at an entry header ("[yyyy-") when one is inside.</summary>
    public static string LastCrashEntries(string logText, int maxChars)
    {
        if (logText.Length <= maxChars)
        {
            return logText;
        }

        var tail = logText[^maxChars..];
        if (EntryHeaderAtStart().IsMatch(tail))
        {
            return tail;
        }

        var header = EntryHeaderAfterNewLine().Match(tail);
        return header.Success ? tail[(header.Index + 1)..] : tail;
    }

    private static BugReportLink Fit(BugReportInput input, Func<BugReportInput, string> build)
    {
        var url = build(input);
        if (url.Length <= MaxUrlLength)
        {
            return new BugReportLink(url, false);
        }

        var log = input.CrashLog ?? "";
        var logKeep = log.Length;
        string[] texts = [input.WhatHappened, input.Steps, input.ExpectedActual];
        var textKeep = texts.Select(t => t.Length).ToArray();
        var titleKeep = input.Title.Length;

        for (var guard = 0; guard < 200; guard++)
        {
            var current = input with
            {
                CrashLog = logKeep <= 0 ? (log.Length > 0 ? TruncatedNote : input.CrashLog) : logKeep < log.Length ? TruncatedNote + NewLine + log[^logKeep..] : log,
                WhatHappened = Head(texts[0], textKeep[0]),
                Steps = Head(texts[1], textKeep[1]),
                ExpectedActual = Head(texts[2], textKeep[2]),
                Title = titleKeep < input.Title.Length ? input.Title[..titleKeep] : input.Title,
            };
            url = build(current);
            var excess = url.Length - MaxUrlLength;
            if (excess <= 0)
            {
                return new BugReportLink(url, true);
            }

            if (logKeep > 0)
            {
                logKeep = Math.Max(0, logKeep - CharsToDrop(excess, log));
            }
            else if (Enumerable.Range(0, 3).Where(i => textKeep[i] > 0).OrderByDescending(i => textKeep[i]).FirstOrDefault(-1) is var longest and >= 0)
            {
                textKeep[longest] = Math.Max(0, textKeep[longest] - CharsToDrop(excess, texts[longest]));
            }
            else
            {
                titleKeep = Math.Max(0, titleKeep - CharsToDrop(excess, input.Title));
            }
        }

        return new BugReportLink(url[..MaxUrlLength], true);
    }

    private static string Head(string text, int keep) => keep < text.Length ? text[..keep] + NewLine + TruncatedNote : text;

    /// <summary>How many characters of <paramref name="text"/> to drop so that about <paramref name="excess"/> encoded characters go.</summary>
    private static int CharsToDrop(int excess, string text)
    {
        if (text.Length == 0)
        {
            return 1;
        }

        var encodedPerChar = Math.Max(1.0, (double)E(text).Length / text.Length);
        return (int)Math.Ceiling(excess / encodedPerChar) + 16;
    }

    private static string E(string text) => Uri.EscapeDataString(text);

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", NewLine);

    /// <summary>Drive letter plus all folder segments up to the last backslash, e.g. "D:\Spiele\Jörg Müller Mods\".</summary>
    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\\\r\n""<>|:*?]+\\)*")]
    private static partial Regex DriveFolders();

    [GeneratedRegex(@"^\[\d{4}-")]
    private static partial Regex EntryHeaderAtStart();

    [GeneratedRegex(@"\n\[\d{4}-")]
    private static partial Regex EntryHeaderAfterNewLine();
}
