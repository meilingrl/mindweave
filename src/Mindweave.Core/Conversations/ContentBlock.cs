using System.Text;
using Mindweave.Core.Resources;

namespace Mindweave.Core.Conversations;

/// <summary>
/// One ordered part of a message's content. A message is a sequence of blocks so that later kinds (tool
/// calls and results, citations, tables, charts) slot in beside text without reshaping messages; source
/// anchors locate a position as (block ordinal, offset within the block). Text and attachments exist today.
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
    /// <summary>The name a person sees: the file name, or the paste placeholder.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ContentBlocks.PastedTextName : Name;
}

public static class ContentBlocks
{
    /// <summary>What an unnamed paste is called where a name is shown.</summary>
    public const string PastedTextName = "粘贴的文字";

    public static IReadOnlyList<ContentBlock> FromText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return [new TextBlock(markdown)];
    }

    /// <summary>
    /// The message as sent in context: text blocks as they are; an attachment with text as a marked section holding that
    /// text; an attachment without text (an image) as a marked placeholder. Parts are joined by a blank line, so a
    /// message of text blocks only reads exactly as before attachments existed.
    /// </summary>
    public static string TextOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.Count == 1 && blocks[0] is TextBlock only
            ? only.Markdown
            : string.Join("\n\n", blocks.Select(ContextTextOf).Where(t => t is not null));
    }

    /// <summary>
    /// The message for titles, excerpts and display: text blocks as they are, each attachment as a one-line
    /// <c>[附件：name]</c>. Never includes an attachment's content.
    /// </summary>
    public static string BriefOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.Count == 1 && blocks[0] is TextBlock only
            ? only.Markdown
            : string.Join("\n\n", blocks.Select(BriefTextOf).Where(t => t is not null));
    }

    /// <summary>The attachment as a marked section of context (see <see cref="TextOf"/>).</summary>
    public static string ContextSection(AttachmentBlock attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var head = new StringBuilder("<attachment name=\"")
            .Append(Attribute(attachment.DisplayName))
            .Append("\" type=\"")
            .Append(Attribute(attachment.MediaType))
            .Append('"');
        if (attachment.Text is null)
        {
            return head.Append(" omitted=\"").Append(MediaTypes.IsImage(attachment.MediaType) ? "image" : "content").Append("\" />").ToString();
        }

        // The content cannot end its own section early: a closing tag inside it is written escaped.
        var body = attachment.Text.Replace("</attachment", "<\\/attachment", StringComparison.OrdinalIgnoreCase);
        return head.Append(">\n").Append(body).Append(body.EndsWith('\n') ? "" : "\n").Append("</attachment>").ToString();
    }

    private static string? ContextTextOf(ContentBlock block) => block switch
    {
        TextBlock t => t.Markdown,
        AttachmentBlock a => ContextSection(a),
        _ => null,
    };

    private static string? BriefTextOf(ContentBlock block) => block switch
    {
        TextBlock t => t.Markdown,
        AttachmentBlock a => $"[附件：{a.DisplayName}]",
        _ => null,
    };

    private static string Attribute(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
