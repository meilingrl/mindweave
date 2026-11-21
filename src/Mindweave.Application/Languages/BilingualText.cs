namespace Mindweave.Application.Languages;

/// <summary>
/// A message held for later in both languages, so it reads in whatever the language is when it is shown — for
/// example a server error kept by a service across a language switch (docs/design/bilingual-ui.md §4.3).
/// </summary>
public sealed record BilingualText(string English, string Chinese)
{
    public override string ToString() => Bilingual.T(English, Chinese);
}
