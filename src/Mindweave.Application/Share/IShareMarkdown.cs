using Mindweave.Application.Exchange;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Share;

/// <summary>
/// A piece of drawn text in a shared page: a text, or an element with its parts. It is never markup, so nothing written in
/// an answer can become an element of the page's. <paramref name="Kind"/> is one of the few kinds the page knows; what else
/// an element needs (an address, a formula, a citation's number) is its <paramref name="Value"/>.
/// </summary>
public sealed record ShareNode(string Kind, IReadOnlyList<ShareNode> Parts, string? Value = null)
{
    /// <summary>The kind of a plain text; its <see cref="Value"/> is the text.</summary>
    public const string TextKind = "text";

    /// <summary>A formula in a sentence, and one on lines of its own; their <see cref="Value"/> is its LaTeX.</summary>
    public const string MathKind = "math";

    public const string MathBlockKind = "mathblock";

    public static ShareNode OfText(string text) => new(TextKind, [], text);
}

/// <summary>A shared branch that left from the text being drawn.</summary>
public sealed record ShareMark(ConversationId Branch, ExchangeBranchSource Source);

/// <summary>
/// What sharing needs to know of Markdown, read exactly as the app reads it (architecture §5, §16.3); implemented where the
/// app draws its answers.
/// </summary>
public interface IShareMarkdown
{
    /// <summary>
    /// The text as the page draws it, each [n] of <paramref name="citations"/> leading to its citation and the sentence of
    /// each mark marked, where it can be told for certain (§6.6, point 3).
    /// </summary>
    IReadOnlyList<ShareNode> Draw(string markdown, IReadOnlySet<int> citations, IReadOnlyList<ShareMark> marks);

    /// <summary>Where the quote starts in the answer's Markdown; null when the stored anchor does not say exactly.</summary>
    int? OffsetOf(BranchSource source);
}
