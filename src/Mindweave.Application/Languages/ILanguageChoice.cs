namespace Mindweave.Application.Languages;

/// <summary>
/// The interface language the user chose, kept outside the database so it is known before the database opens and is not
/// carried by a backup restore (docs/design/bilingual-ui.md §4.1, §7). Anything missing or unreadable means English.
/// </summary>
public interface ILanguageChoice
{
    /// <summary>Read once at startup, before the first window.</summary>
    UiLanguage Read();

    Task<UiLanguage> ReadAsync();

    /// <summary>Whether the choice was saved; a failure leaves the previous choice in place.</summary>
    Task<bool> WriteAsync(UiLanguage language);
}
