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
    public static string Whole(AttachmentBlock attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ContentBlocks.Section(attachment, "whole", $"以下是用户附上的材料《{attachment.DisplayName}》的全文：\n\n{attachment.Text}");
    }

    /// <summary>A text resource as its summary only (an older question's attachment that did not fit whole).</summary>
    public static string Summary(AttachmentBlock attachment, string summary)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return ContentBlocks.Section(attachment, "summary", $"以下是用户附上的材料《{attachment.DisplayName}》的摘要（全文较长，这里只有摘要）：\n\n{summary}");
    }

    /// <summary>
    /// A text resource as its summary and the chunks chosen for the question, in the text's order and numbered from 1 out
    /// of <paramref name="chunkCount"/>.
    /// </summary>
    public static string Parts(AttachmentBlock attachment, string summary, IReadOnlyList<(int Ordinal, string Text)> chunks, int chunkCount)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(chunks);
        var body = new System.Text.StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"以下是用户附上的材料《{attachment.DisplayName}》的摘要，以及其中与问题最相关的部分（全文共 {chunkCount} 部分，这里有第 {PartList(chunks.Select(c => c.Ordinal))} 部分）。\n\n摘要：\n")
            .Append(summary);
        foreach (var (ordinal, text) in chunks.OrderBy(c => c.Ordinal))
        {
            body.Append(CultureInfo.InvariantCulture, $"\n\n{ChunkHeading(ordinal)}\n").Append(text);
        }

        return ContentBlocks.Section(attachment, "parts", body.ToString());
    }

    /// <summary>The heading before one chunk in <see cref="Parts"/>; its cost is counted with the chunk.</summary>
    public static string ChunkHeading(int ordinal) => $"[第 {ordinal + 1} 部分]";

    /// <summary>Tokens a chunk adds to <see cref="Parts"/>: its text and heading.</summary>
    public static int ChunkTokens(ResourceChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return chunk.EstimatedTokens + TokenEstimator.Estimate(ChunkHeading(chunk.Ordinal) + "\n\n\n");
    }

    /// <summary>The opening of <see cref="Parts"/> before the summary: what it costs beyond the summary-only form, at most.</summary>
    public const int PartsOverheadTokens = 60;

    /// <summary>An image the current model cannot see (requirements §10.4.3).</summary>
    public static string ImageUnsupported(AttachmentBlock attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return $"[图片：{attachment.DisplayName}，当前模型不能查看]";
    }

    /// <summary>An older image left out because the context had no room for it.</summary>
    public static string ImageOmitted(AttachmentBlock attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return $"[图片：{attachment.DisplayName}，较早的图片，因篇幅未再发送]";
    }

    /// <summary>The line naming an image sent as an image part, so the model can tell which image is which.</summary>
    public static string ImageLabel(AttachmentBlock attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return $"[图片：{attachment.DisplayName}]";
    }

    /// <summary>"3、5、7" for ordinals 2, 4, 6.</summary>
    public static string PartList(IEnumerable<int> ordinals)
    {
        ArgumentNullException.ThrowIfNull(ordinals);
        return string.Join('、', ordinals.Order().Select(o => (o + 1).ToString(CultureInfo.InvariantCulture)));
    }
}
