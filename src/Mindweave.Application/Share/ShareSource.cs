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
    bool IsAnswering);

/// <summary>What of a workspace can be shared: no message text, no workspace name, no state of the user's.</summary>
public sealed record ShareSource(IReadOnlyList<ShareableConversation> Conversations, IReadOnlyList<ExchangeLink> Links)
{
    public IReadOnlyDictionary<ConversationId, ConversationId?> Parents => Conversations.ToDictionary(c => c.Id, c => c.Parent);
}
