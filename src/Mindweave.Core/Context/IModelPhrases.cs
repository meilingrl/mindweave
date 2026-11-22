using Mindweave.Core.Conversations;

namespace Mindweave.Core.Context;

/// <summary>
/// The short text Core puts into what goes to the model (docs/design/bilingual-ui.md §5): material headers, image
/// placeholders, attachment markers, citation labels, the framing of a context summary and of a branch's quoted question.
/// Core holds no language: the caller passes the set for the language a request is built in (Application's
/// <c>ModelPhrases.Current</c>). Core keeps the structure (section tags, line breaks, quoting); a set supplies only words.
/// </summary>
public interface IModelPhrases
{
    /// <summary>The name of a pasted text that has none of its own.</summary>
    string PastedText { get; }

    /// <summary>An attachment as one line of its message's text, such as <c>[Attachment: name]</c>.</summary>
    string AttachmentMarker(string name);

    /// <summary>The line opening a whole text attachment; its text follows after a blank line.</summary>
    string MaterialWhole(string name);

    /// <summary>The line opening an attachment sent as its digest only; the digest follows after a blank line.</summary>
    string MaterialDigest(string name);

    /// <summary>
    /// The line opening an attachment sent as its digest and some of its parts: <paramref name="parts"/> are the parts'
    /// numbers (from 1, ascending) out of <paramref name="chunkCount"/>. <see cref="DigestHeading"/> and the digest follow.
    /// </summary>
    string MaterialDigestAndParts(string name, int chunkCount, IReadOnlyList<int> parts);

    /// <summary>The heading before the digest in the digest-and-parts form.</summary>
    string DigestHeading { get; }

    /// <summary>The heading before one part, numbered from 1.</summary>
    string ChunkHeading(int number);

    /// <summary>An image the current model cannot view (requirements §10.4.3).</summary>
    string ImageUnviewable(string name);

    /// <summary>An older image left out because the context had no room for it.</summary>
    string ImageNotResent(string name);

    /// <summary>The line naming an image sent as an image part.</summary>
    string Image(string name);

    /// <summary>A citation's name in a citation list: a web page's title as it is, or the kind of material and its title.</summary>
    string CitationLabel(CitationKind kind, string title);

    /// <summary>The heading of an answer's citation list in its context text.</summary>
    string CitationsHeading { get; }

    /// <summary>The line opening a context summary of messages <paramref name="first"/> to <paramref name="last"/> (from 1).</summary>
    string EarlierSummarized(int first, int last);

    /// <summary>A branch's first question: <paramref name="quoted"/> is the passage already quoted line by line ("> ").</summary>
    string QuotedQuestion(string quoted, string question);
}
