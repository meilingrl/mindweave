using System.Globalization;

namespace Mindweave.Application.Languages;

/// <summary>
/// Text written once with both languages side by side (docs/design/bilingual-ui.md §4.2): <c>T("Cancel", "取消")</c>.
/// Leaving out a language does not compile, and each language keeps its own grammar.
/// </summary>
public static class Bilingual
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>The text in the current language.</summary>
    public static string T(string english, string chinese) =>
        CurrentLanguage.Value == UiLanguage.English ? english : chinese;

    /// <summary>An English count with its noun: "1 conversation", "3 conversations".</summary>
    public static string Plural(int count, string one, string many) =>
        count.ToString(English) + " " + (count == 1 ? one : many);

    /// <summary>How numbers and dates are written in English text, whatever the current language.</summary>
    public static CultureInfo EnglishCulture => English;

    /// <summary>How numbers and dates are written in the current language.</summary>
    public static CultureInfo Culture => CurrentLanguage.Value == UiLanguage.English ? English : Chinese;
}
