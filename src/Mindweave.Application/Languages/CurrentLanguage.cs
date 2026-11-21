namespace Mindweave.Application.Languages;

/// <summary>
/// The one interface language of this process. It is set before the first view is built and changed only by a
/// language switch, which rebuilds the views (docs/design/bilingual-ui.md §6), so text is read, never subscribed to.
/// </summary>
public static class CurrentLanguage
{
    private static volatile UiLanguage _value = UiLanguage.English;

    public static UiLanguage Value => _value;

    public static void Set(UiLanguage language) => _value = language;
}
