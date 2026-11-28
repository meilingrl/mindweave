using Mindweave.Application.Languages;

namespace Mindweave.Application.Prompts;

/// <summary>
/// Prompt texts that stand in for the embedded ones while <see cref="PromptLibrary.Use"/> is in effect, by prompt name and
/// language. A prompt it does not name keeps its embedded text; an empty text leaves that prompt out where the code allows
/// it (the answer instructions, for an edition from before they existed).
/// </summary>
/// <param name="Name">What the evaluation report calls it.</param>
public sealed record PromptEdition(string Name, IReadOnlyDictionary<string, string> English, IReadOnlyDictionary<string, string> Chinese)
{
    /// <summary>This edition's text of <paramref name="name"/>, or null when the embedded one stands.</summary>
    public string? TextOf(string name, UiLanguage language) =>
        (language == UiLanguage.English ? English : Chinese).TryGetValue(name, out var text) ? text : null;
}
