using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RnwTileGenerator.App;

/// <summary>The 7 UI languages: English (base/default) plus the 6
/// requested additions. German folds in - and replaces - the ad-hoc German
/// tooltip/label strings that were written directly into the UI in an
/// earlier round, now routed through this same key/translation system
/// instead of being hardcoded.</summary>
public enum AppLanguage { English, German, Spanish, French, Italian, Russian, ChineseSimplified }

/// <summary>
/// Small key -&gt; per-language string lookup, in the same spirit as
/// BrushPresets.cs: a tiny JSON file next to the .exe remembers the
/// chosen language between runs, everything else lives in memory. Deliberately
/// simple (no .resx, no satellite assemblies, no third-party i18n
/// library) to keep the project's zero-NuGet-dependency and
/// single-executable properties intact.
///
/// Coverage is intentionally not 100% of every string in the app: the
/// main chrome (menus, tab labels, toolbar, landing screen, dialog
/// buttons), the shared brush-size control, the Random Generation and
/// Heightmap Import screens, and each stage's title/tool/mode labels all
/// go through Loc.T(...) - the longer explanatory paragraphs
/// (Ui.Wrap(...) usage-instructions text) and the more technical/rarely
/// seen Metadata and Export stage fields stay as authored (German, from
/// an earlier round, or English) regardless of the chosen UI language.
/// See claude/status.md for the exact list - the key/dictionary pattern
/// here makes extending coverage later a matter of adding more Loc.Add(...)
/// calls in LocalizationStrings.cs, not a structural change.
/// </summary>
public static class Loc
{
    public static event Action? LanguageChanged;

    private static readonly Dictionary<AppLanguage, string> DisplayNames = new()
    {
        [AppLanguage.English] = "English",
        [AppLanguage.German] = "Deutsch",
        [AppLanguage.Spanish] = "Español",
        [AppLanguage.French] = "Français",
        [AppLanguage.Italian] = "Italiano",
        [AppLanguage.Russian] = "Русский",
        [AppLanguage.ChineseSimplified] = "简体中文",
    };

    public static IReadOnlyList<AppLanguage> AllLanguages { get; } = new[]
    {
        AppLanguage.English, AppLanguage.German, AppLanguage.Spanish,
        AppLanguage.French, AppLanguage.Italian, AppLanguage.Russian, AppLanguage.ChineseSimplified,
    };

    public static string DisplayName(AppLanguage l) => DisplayNames.TryGetValue(l, out var n) ? n : l.ToString();

    private static readonly Dictionary<string, Dictionary<AppLanguage, string>> Strings = new();

    /// <summary>Registers one key's translation across all 7 languages.
    /// Called only from LocalizationStrings.Register() during the static
    /// constructor below.</summary>
    internal static void Add(string key, string en, string de, string es, string fr, string it, string ru, string zh)
    {
        Strings[key] = new Dictionary<AppLanguage, string>
        {
            [AppLanguage.English] = en,
            [AppLanguage.German] = de,
            [AppLanguage.Spanish] = es,
            [AppLanguage.French] = fr,
            [AppLanguage.Italian] = it,
            [AppLanguage.Russian] = ru,
            [AppLanguage.ChineseSimplified] = zh,
        };
    }

    /// <summary>Looks up `key` in the current language, falling back to
    /// English, and finally to the raw key itself (so a missing
    /// translation is visibly obvious rather than crashing the UI).</summary>
    public static string T(string key)
    {
        if (Strings.TryGetValue(key, out var perLang))
        {
            if (perLang.TryGetValue(Current, out var s) && !string.IsNullOrEmpty(s)) return s;
            if (perLang.TryGetValue(AppLanguage.English, out var en) && !string.IsNullOrEmpty(en)) return en;
        }
        return key;
    }

    public static AppLanguage Current { get; private set; } = AppLanguage.English;

    public static void SetLanguage(AppLanguage lang)
    {
        if (Current == lang) return;
        Current = lang;
        SaveSetting(lang);
        LanguageChanged?.Invoke();
    }

    /// <summary>Switches the language for this process only: nothing is saved, no LanguageChanged event.
    /// Used by the update mode and by the checks project.</summary>
    internal static void UseLanguage(AppLanguage lang) => Current = lang;

    /// <summary>The update mode runs from the staging folder, which has no language.json; it shows its
    /// window in the language saved in the installed program folder instead (read only, never written).</summary>
    internal static void UseLanguageOf(string programFolder) => Current = LoadSetting(Path.Combine(programFolder, "language.json"));

    /// <summary>All registered keys with their translations (for the checks project).</summary>
    internal static IEnumerable<KeyValuePair<string, IReadOnlyDictionary<AppLanguage, string>>> Entries
    {
        get
        {
            foreach (var pair in Strings)
                yield return new KeyValuePair<string, IReadOnlyDictionary<AppLanguage, string>>(pair.Key, pair.Value);
        }
    }

    // -- persistence (best-effort, same pattern as BrushPresets.cs) -----------

    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "language.json");

    private static AppLanguage LoadSetting() => LoadSetting(FilePath);

    private static AppLanguage LoadSetting(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null && data.TryGetValue("language", out var name) && Enum.TryParse<AppLanguage>(name, out var lang))
                    return lang;
            }
        }
        catch
        {
            // Corrupt/unreadable - fall back to English rather than crash.
        }
        return AppLanguage.English;
    }

    private static void SaveSetting(AppLanguage lang)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["language"] = lang.ToString() }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best effort - the choice just resets to English next launch.
        }
    }

    static Loc()
    {
        LocalizationStrings.Register();
        Current = LoadSetting();
    }
}
