using Mindweave.Application.Exchange;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Share;

/// <summary>
/// A conversation the share dialog offers, already as it would leave: its name never holds message text, so a
/// conversation without a title has none (docs/design/sharing.md §6.6, points 1 and 2).
/// </summary>
/// <param name="Name">Its title, or its summary's name as the Mindweave page shows it; null when it has neither.</param>
/// <param name="Questions">How many questions were asked in it, up to the last finished answer (§9).</param>
/// <param name="IsAnswering">An answer is still being generated in it.</param>
public sealed record ShareableConversation(
    ConversationId Id,
    ConversationId? Parent,
    string? Name,
    DateTimeOffset StartedAt,
    int Questions,
    bool HasSummary,
    bool IsAnswering)
{
    /// <summary>
    /// The name the app uses for it, which can be the sentence it branched off from or its first question: only a share
    /// with content carries it (§6.6, point 1).
    /// </summary>
    public string? ContentName { get; init; } = Name;
}

/// <summary>What of a workspace can be shared: no message text beyond a name, no workspace name, no state of the user's.</summary>
public sealed record ShareSource(IReadOnlyList<ShareableConversation> Conversations, IReadOnlyList<ExchangeLink> Links)
{
    public IReadOnlyDictionary<ConversationId, ConversationId?> Parents => Conversations.ToDictionary(c => c.Id, c => c.Parent);
}
