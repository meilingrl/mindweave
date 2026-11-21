using System.Globalization;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;

namespace Mindweave.Core.Context;

/// <summary>
/// How a resource is written into a request (architecture §15.4): a bounded section that opens with a line naming the material
/// and the form it is sent in, and closes with an end tag. The planner counts these texts; the request sends exactly them.
/// </summary>
public static class ResourceMaterial
{
    /// <summary>Estimated tokens one image costs as a request part (architecture §15.4), before the model's calibration.</summary>
    public const int ImageTokens = 1_600;

    /// <summary>A whole text resource.</summary>
    public static string Whole(AttachmentBlock attachment, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(phrases);
        return ContentBlocks.Section(attachment, phrases, "whole", $"{phrases.MaterialWhole(attachment.NameIn(phrases))}\n\n{attachment.Text}");
    }

    /// <summary>A text resource as its summary only (an older question's attachment that did not fit whole).</summary>
    public static string Summary(AttachmentBlock attachment, string summary, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(phrases);
        return ContentBlocks.Section(attachment, phrases, "summary", $"{phrases.MaterialDigest(attachment.NameIn(phrases))}\n\n{summary}");
    }

    /// <summary>
    /// A text resource as its summary and the chunks chosen for the question, in the text's order and numbered from 1 out
    /// of <paramref name="chunkCount"/>.
    /// </summary>
    public static string Parts(AttachmentBlock attachment, string summary, IReadOnlyList<(int Ordinal, string Text)> chunks, int chunkCount, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(phrases);
        var numbers = chunks.Select(c => c.Ordinal + 1).Order().ToList();
        var body = new System.Text.StringBuilder()
            .Append(phrases.MaterialDigestAndParts(attachment.NameIn(phrases), chunkCount, numbers))
            .Append("\n\n")
            .Append(phrases.DigestHeading)
            .Append('\n')
            .Append(summary);
        foreach (var (ordinal, text) in chunks.OrderBy(c => c.Ordinal))
        {
            body.Append(CultureInfo.InvariantCulture, $"\n\n{ChunkHeading(ordinal, phrases)}\n").Append(text);
        }

        return ContentBlocks.Section(attachment, phrases, "parts", body.ToString());
    }

    /// <summary>The heading before one chunk in <see cref="Parts"/>; its cost is counted with the chunk.</summary>
    public static string ChunkHeading(int ordinal, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(phrases);
        return phrases.ChunkHeading(ordinal + 1);
    }

    /// <summary>Tokens a chunk adds to <see cref="Parts"/>: its text and heading.</summary>
    public static int ChunkTokens(ResourceChunk chunk, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.EstimatedTokens + TokenEstimator.Estimate(ChunkHeading(chunk.Ordinal, phrases) + "\n\n\n");
    }

    /// <summary>
    /// The opening of <see cref="Parts"/> before the summary: what it costs beyond the summary-only form, at most, in either
    /// language's phrases (checked by the phrase tests).
    /// </summary>
    public const int PartsOverheadTokens = 60;

    /// <summary>An image the current model cannot see (requirements §10.4.3).</summary>
    public static string ImageUnsupported(AttachmentBlock attachment, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(phrases);
        return phrases.ImageUnviewable(attachment.NameIn(phrases));
    }

    /// <summary>An older image left out because the context had no room for it.</summary>
    public static string ImageOmitted(AttachmentBlock attachment, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(phrases);
        return phrases.ImageNotResent(attachment.NameIn(phrases));
    }

    /// <summary>The line naming an image sent as an image part, so the model can tell which image is which.</summary>
    public static string ImageLabel(AttachmentBlock attachment, IModelPhrases phrases)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(phrases);
        return phrases.Image(attachment.NameIn(phrases));
    }

    /// <summary>"3、5、7" for ordinals 2, 4, 6: the Chinese list of part numbers.</summary>
    public static string PartList(IEnumerable<int> ordinals)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        return string.Join('、', ordinals.Order().Select(o => (o + 1).ToString(CultureInfo.InvariantCulture)));
    }
}
