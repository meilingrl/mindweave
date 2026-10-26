namespace Mindweave.Core.Conversations;

/// <summary>
/// One ordered part of a message's content. A message is a sequence of blocks so that later kinds (tool
/// calls and results, citations, tables, charts) slot in beside text without reshaping messages; source
/// anchors locate a position as (block ordinal, offset within the block). Only text exists today.
/// </summary>
public abstract record ContentBlock
{
    private protected ContentBlock()
    {
    }
}

/// <summary>Markdown source.</summary>
public sealed record TextBlock(string Markdown) : ContentBlock;

public static class ContentBlocks
{
    public static IReadOnlyList<ContentBlock> FromText(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return [new TextBlock(markdown)];
    }

    /// <summary>
    /// The message as plain Markdown: its text blocks joined by a blank line. This is what is sent as
    /// context and used for titles; other block kinds get their own serialization when they are added.
    /// </summary>
    public static string TextOf(IReadOnlyList<ContentBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        return blocks.Count == 1 && blocks[0] is TextBlock only
            ? only.Markdown
            : string.Join("\n\n", blocks.OfType<TextBlock>().Select(b => b.Markdown));
    }
}
