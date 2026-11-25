using System.Text;
using Mindweave.Core.Context;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;

namespace Mindweave.Core.Conversations;

/// <summary>
/// One ordered part of a message's content. A message is a sequence of blocks so that a further kind (tables, charts)
/// slots in beside those below without reshaping messages; source anchors locate a position as (block ordinal, offset
/// within the block).
/// </summary>
public abstract record ContentBlock
{
    private protected ContentBlock()
    {
    }
}

/// <summary>Markdown source.</summary>
public sealed record TextBlock(string Markdown) : ContentBlock;

/// <summary>
/// A reference to a stored <see cref="Resource"/> (docs/design/resources.md): where and when the material entered the
/// conversation. <paramref name="Name"/> is the file name, null for a paste; <paramref name="Text"/> is the resource's
/// extracted text, null when it has none (an image). Storage fills the descriptive fields from the resource on read, so
/// only <paramref name="Resource"/> is authoritative when a block is written.
/// </summary>
public sealed record AttachmentBlock(ResourceId Resource, string? Name, string MediaType, long Size, string? Text) : ContentBlock
{
    /// <summary>The name the model reads: the file name, or the paste placeholder in <paramref name="phrases"/>.</summary>
    public string NameIn(IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        return string.IsNullOrWhiteSpace(Name) ? phrases.PastedText : Name;
    }
}

/// <summary>
/// Stands for an attachment that did not travel in a share (docs/design/sharing-import.md §3): its name as the share had it,
/// null for a pasted text. There is nothing to open and nothing to send.
/// </summary>
public sealed record AbsentAttachmentBlock(string? Name) : ContentBlock;

/// <summary>
/// A tool call the model made during a run (architecture §15.3): its call ID, the tool's name, the arguments as the model
/// sent them (JSON text, possibly invalid), the tool's risk, and <paramref name="Summary"/>, one readable line for step
/// details. Never part of the text sent as context later (<see cref="ContentBlocks.TextOf"/>).
/// </summary>
public sealed record ToolCallBlock(string CallId, string Tool, string Arguments, ToolRisk Risk, string Summary) : ContentBlock;

/// <summary>
/// The result of one tool call: <paramref name="Preview"/> is a short preview (or the error), <paramref name="Resource"/> the
/// stored full result (null when the call failed), <paramref name="Detail"/> a short readable account ("找到 3 段"), and
/// <paramref name="Findings"/> what the result points at (summaries found, attachment parts read), for step details.
/// </summary>
public sealed record ToolResultBlock(string CallId, ToolResultStatus Status, string Preview, ResourceId? Resource, string? Detail, IReadOnlyList<ToolFinding>? Findings = null) : ContentBlock;

/// <summary>
/// Stands for the tool steps of a received answer (docs/design/sharing-import.md §3): how often it used tools, by kind. The
/// steps themselves did not travel. Never part of the text sent as context.
/// </summary>
public sealed record ActivityCountBlock(int AttachmentSearches, int SummarySearches, int WebSearches, int PagesRead, int OtherCalls) : ContentBlock;

/// <summary>What a 出处 is (docs/design/agent-loop.md L11): a web page or search result, or material of this app.</summary>
public enum CitationKind
{
    /// <summary>A search result or a page read from the web; it has a real address.</summary>
    Web,

    /// <summary>A part of a file or text the user attached.</summary>
    Attachment,

    /// <summary>A part of material an earlier run found (its tool results), read again.</summary>
    Material,

    /// <summary>A saved summary of the workspace (检索总结).</summary>
    Summary,
}

/// <summary>
/// One 出处 of an answer (requirements §10.4.5, architecture §15.9 and §15.13): the number the answer cites as [n], the
/// source's title and, for the web, its real address and when it was fetched; the stored resource and the cited passage as a
/// character range of the resource's text (<paramref name="Start"/> inclusive, <paramref name="End"/> exclusive). Since L11
/// a run's local results are numbered too: <see cref="Kind"/> says what the source is, and a summary's node is
/// <see cref="Target"/>. Written when the answer ends, after its tool blocks; only numbers the text cites and the run really
/// found have one. A received answer's 出处 has no stored material (<see cref="WithoutMaterial"/>): neither a time nor a resource.
/// </summary>
public sealed record CitationBlock(int Number, string Title, Uri? Url, DateTimeOffset? FetchedAt, ResourceId? Resource, int Start, int End) : ContentBlock
{
    /// <summary>
    /// A 出处 as a share carries it (docs/design/sharing-import.md §3): a web page's title and address; for the sharer's own
    /// material its kind, and a title only for a summary that is in the share. Without a title <see cref="Title"/> is empty.
    /// </summary>
    public static CitationBlock WithoutMaterial(int number, CitationKind kind, string? title, Uri? url) =>
        new(number, title ?? string.Empty, url, null, null, 0, 0) { Kind = kind };

    /// <summary>There is something behind it: stored material, or an address for the browser. Otherwise it is plain text.</summary>
    public bool CanBeOpened => Resource is not null || Url is not null;

    public CitationKind Kind { get; init; } = CitationKind.Web;

    /// <summary>For a summary: its node's ID (<c>D</c> format).</summary>
    public string? Target { get; init; }

    /// <summary>The name in a citation list sent to the model: the page's title, or the kind of material and its title.</summary>
    public string Label(IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        return phrases.CitationLabel(Kind, Title);
    }

    /// <summary>The line it is as text (context and summaries): "[1] title https://…", or "[2]" and the attachment's label.</summary>
    public string Line(IModelPhrases phrases) => Url is { } url ? $"[{Number}] {Label(phrases)} {url.AbsoluteUri}" : $"[{Number}] {Label(phrases)}";
}

public static class ContentBlocks
{
    public static IReadOnlyList<ContentBlock> FromText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return [new TextBlock(markdown)];
    }

    /// <summary>
    /// The message as its conversation layer sends it (architecture §15.3): text blocks as they are, each attachment as a
    /// one-line marker (<see cref="IModelPhrases.AttachmentMarker"/>). An attachment's content is sent by the resource layer (<see cref="Context.ResourceMaterial"/>),
    /// planned apart from the conversation, so no single message can outgrow the context window. Parts are joined by a
    /// blank line, so a message of text blocks only reads exactly as before attachments existed.
    /// </summary>
    public static string TextOf(IReadOnlyList<ContentBlock> blocks, IModelPhrases phrases) => BriefOf(blocks, phrases);

    /// <summary>
    /// The message as conversation context and summary input (architecture §15.4, §15.9): <see cref="TextOf"/>, then an
    /// answer's 出处 as one list ("[n] title URL"), so what it relied on goes along without the pages themselves. The
    /// answer's own text (anchors, display) never includes the list.
    /// </summary>
    public static string ContextTextOf(IReadOnlyList<ContentBlock> blocks, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        var text = TextOf(blocks, phrases);
        var citations = CitationsOf(blocks);
        return citations.Count == 0 ? text : text + "\n\n" + phrases.CitationsHeading + "\n" + string.Join("\n", citations.Select(c => c.Line(phrases)));
    }

    /// <summary>
    /// The message for titles, excerpts and display: text blocks as they are, each attachment as a one-line marker
    /// (<see cref="IModelPhrases.AttachmentMarker"/>). Never includes an attachment's content.
    /// </summary>
    public static string BriefOf(IReadOnlyList<ContentBlock> blocks, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(phrases);
        return blocks.Count == 1 && blocks[0] is TextBlock only
            ? only.Markdown
            : string.Join("\n\n", blocks.Select(b => BriefTextOf(b, phrases)).Where(t => t is not null));
    }

    /// <summary>The text the user typed, without attachments.</summary>
    public static string TypedTextOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return string.Join("\n\n", blocks.OfType<TextBlock>().Select(t => t.Markdown));
    }

    /// <summary>The run's tool calls and results in step order (an answer's blocks after its text).</summary>
    public static IEnumerable<ContentBlock> ToolBlocksOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.Where(b => b is ToolCallBlock or ToolResultBlock);
    }

    /// <summary>An answer's 出处, in number order.</summary>
    public static IReadOnlyList<CitationBlock> CitationsOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return [.. blocks.OfType<CitationBlock>().OrderBy(c => c.Number)];
    }

    /// <summary>The names of everything that was attached, in order, what did not travel included; null for a pasted text.</summary>
    public static IReadOnlyList<string?> AttachmentNamesOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return [.. blocks.Where(b => b is AttachmentBlock or AbsentAttachmentBlock).Select(b => (b as AttachmentBlock)?.Name ?? (b as AbsentAttachmentBlock)?.Name)];
    }

    public static IEnumerable<AttachmentBlock> AttachmentsOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.OfType<AttachmentBlock>();
    }

    /// <summary>One attachment as its brief line.</summary>
    public static string BriefOf(AttachmentBlock attachment, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(phrases);
        return phrases.AttachmentMarker(attachment.NameIn(phrases));
    }

    /// <summary>A material section: an opening tag naming the attachment, <paramref name="body"/>, and the closing tag.</summary>
    public static string Section(AttachmentBlock attachment, IModelPhrases phrases, string form, string body)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(body);
        var head = new StringBuilder("<attachment name=\"")
            .Append(Attribute(attachment.NameIn(phrases)))
            .Append("\" type=\"")
            .Append(Attribute(attachment.MediaType))
            .Append("\" form=\"")
            .Append(form)
            .Append("\">\n");

        // The content cannot end its own section early: a closing tag inside it is written escaped.
        var escaped = body.Replace("</attachment", "<\\/attachment", StringComparison.OrdinalIgnoreCase);
        return head.Append(escaped).Append(escaped.EndsWith('\n') ? "" : "\n").Append("</attachment>").ToString();
    }

    private static string? BriefTextOf(ContentBlock block, IModelPhrases phrases) => block switch
    {
        TextBlock t => t.Markdown,
        AttachmentBlock a => BriefOf(a, phrases),
        AbsentAttachmentBlock => phrases.AttachmentNotIncluded,
        _ => null,
    };

    private static string Attribute(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
