using Mindweave.Application.Languages;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The one rule that names a conversation: its title, else (for a branch) the quoted passage it explores, else its first
/// question cut to one line, else "新会话". The sidebar, the page header, the ancestor path and the summary network all
/// use it, so a conversation is named the same everywhere. Read in the language of the moment.
/// </summary>
public static class ConversationNames
{
    public static string NewConversation => Bilingual.T("New conversation", "新会话");

    /// <summary>A quoted passage on one line, cut to the fallback title's length.</summary>
    public static string Quoted(string quote) => Bilingual.T(
        "“" + ConversationRules.Shorten(quote, ConversationRules.FallbackTitleLength) + "”",
        "「" + ConversationRules.Shorten(quote, ConversationRules.FallbackTitleLength) + "」");

    public static string Of(string? title, string? quote, string? firstQuestion) =>
        title
        ?? (quote is not null ? Quoted(quote) : null)
        ?? (firstQuestion is not null ? ConversationRules.FallbackTitle(firstQuestion) : NewConversation);
}
