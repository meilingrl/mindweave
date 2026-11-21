using System.Globalization;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Resources;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Desktop.Text;

// Attachments (requirements §10.4.3, agent platform phase 1): the composer's "+", attachment chips, the viewer, and the
// note under an answer that got only parts or a summary of an attachment.
public static partial class UiText
{
    public static string AttachMenu => T("Add attachment", "添加附件");
    public static string AttachFile => T("File…", "文件…");
    public static string AttachImage => T("Image…", "图片…");
    public static string ImagesUnavailable => T("The current model can’t view images; you can declare image support in Settings › Model connection › Advanced settings", "当前模型不能查看图片，可在设置 › 模型连接 › 高级设置中声明");
    public static string PastedImageRefused => T("The current model can’t view images, so the image wasn’t attached. You can declare image support in Settings › Model connection › Advanced settings.", "当前模型不能查看图片，图片没有附加。可在设置 › 模型连接 › 高级设置中声明。");
    public static string TooManyAttachmentsNotice => T("A question can have at most 10 attachments.", "一条提问最多带 10 个附件。");
    public static string RemoveAttachment => T("Remove", "移除");
    public static string ConvertToText => T("Convert to text", "转为文字");
    public static string RetryAttachment => T("Retry", "重试");
    public static string OpenAttachment => T("View", "查看");
    public static string Extracting => T("Reading…", "正在读取…");
    public static string OriginalMissing => T("Original missing", "原件缺失");
    public static string OriginalMissingNote => T("The attachment’s original is no longer in the data folder. The extracted text can still be viewed and sent.", "附件的原件已不在数据目录中。已提取的文字仍可查看和发送。");
    public static string ImageOriginalMissingNote => T("The image’s original is no longer in the data folder, so it can’t be shown. A text description is sent in its place.", "图片的原件已不在数据目录中，无法显示；发送时以文字说明代替。");
    public static string TruncatedNote => T("The text extracted from this material is over 2 million characters; only the first 2 million were kept.", "这份材料提取出的文字超过 200 万字，只保存了前 200 万字。");
    public static string ViewerCopyAll => T("Copy all", "复制全部");
    public static string ViewerClose => T("Close", "关闭");
    public static string DropHint => T("Drop to attach", "松开即可附加");
    public static string HistoryImagesUnseen => T("Images in this conversation will be sent as text descriptions because the current model can’t view images","会话中的图片会以文字说明代替发送：当前模型不能查看图片");
    public static string FileDialogTitle => T("Choose files to attach", "选择要附加的文件");
    public static string ImageDialogTitle => T("Choose images to attach", "选择要附加的图片");
    public static string AttachmentsFileFilter => T("Text, PDF and Word", "文本、PDF 与 Word");
    public static string ImagesFileFilter => T("Images", "图片");

    /// <summary>Why an attachment could not be added, on its chip (requirements §10.4.3).</summary>
    public static string AttachmentProblem(ResourceCreateStatus status, string? detail = null, bool image = false) => status switch
    {
        ResourceCreateStatus.Unsupported => T(
            string.IsNullOrEmpty(detail) || !detail.StartsWith('.') ? "This type of file isn’t supported" : $"{detail} files aren’t supported",
            string.IsNullOrEmpty(detail) || !detail.StartsWith('.') ? "不支持这种文件" : $"不支持 {detail} 文件"),
        ResourceCreateStatus.TooLarge => T(image ? "The image is over 20 MB or has too many pixels" : "The file is over 50 MB", image ? "图片超过 20 MB 或像素过多" : "文件超过 50 MB"),
        ResourceCreateStatus.NotReadable => T(image ? "Can’t read this image" : "Can’t read the text in it", image ? "无法读取这张图片" : "无法读取其中的文字"),
        ResourceCreateStatus.NoText => T("No text to extract (it may be a scan)", "没有可提取的文字（可能是扫描件）"),
        ResourceCreateStatus.Encrypted => T("The file is password-protected", "文件有密码保护"),
        ResourceCreateStatus.FileUnavailable => T("Can’t open this file", "无法打开这个文件"),
        ResourceCreateStatus.Empty => T("The content is empty", "内容是空的"),
        _ => T("Can’t attach", "无法附加"),
    };

    public static string AttachmentSaveFailed => T("Couldn’t save; the disk may be full", "保存失败，可能是磁盘已满");

    /// <summary>"约 1.2 万字" or "约 3,400 字" for a text; "1.2 MB" for a file.</summary>
    public static string AttachmentSize(int characters) =>
        T(
            characters >= 9_500
                ? $"about {((characters + 500) / 1_000 * 1_000).ToString("N0", EnglishCulture)} characters"
                : characters >= 1_000 ? $"about {((characters + 50) / 100 * 100).ToString("N0", EnglishCulture)} characters" : Plural(characters, "character", "characters"),
            characters >= 9_500
                ? $"约 {(characters / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture)} 万字"
                : characters >= 1_000 ? $"约 {((characters + 50) / 100 * 100).ToString("N0", CultureInfo.InvariantCulture)} 字" : $"{characters} 字");

    public static string ByteSize(long bytes) =>
        bytes >= 1_000_000 ? $"{(bytes / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} MB"
        : bytes >= 1_000 ? $"{(bytes / 1_000.0).ToString("0", CultureInfo.InvariantCulture)} KB"
        : T(Plural((int)bytes, "byte", "bytes"), $"{bytes} 字节");

    /// <summary>A short type word for a chip.</summary>
    public static string AttachmentType(string mediaType, bool paste) =>
        paste ? T("Pasted text", "文字")
        : mediaType == MediaTypes.Pdf ? "PDF"
        : mediaType == MediaTypes.Docx ? "Word"
        : MediaTypes.IsImage(mediaType) ? T("Image", "图片")
        : mediaType == MediaTypes.Markdown ? "Markdown"
        : mediaType is "text/csv" or "text/tab-separated-values" ? T("Tabular text", "表格文字")
        : mediaType is MediaTypes.PlainText ? T("Text", "文本")
        : T("Code", "代码");

    // The note under an answer whose request sent only parts or a summary of an attachment (§10.4.3, item 3 of 发送什么).
    public static string AttachmentReducedNote => T("The attachment is long; only the parts relevant to the question were sent", "附件较长，只发送了与问题相关的部分");
    public static string AttachmentSummaryNote => T("Only digests of earlier attachments were sent", "较早的附件只发送了摘要");
    public static string AttachmentReducedDetails => T("What was sent", "发送了哪些");

    public static string AttachmentNoteOf(IReadOnlyList<ResourceInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return inputs.Any(i => i.Form == ResourceForm.Parts) ? AttachmentReducedNote : AttachmentSummaryNote;
    }

    /// <summary>One line per attachment: what of it went out.</summary>
    public static string AttachmentInputLine(ResourceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Form switch
        {
            ResourceForm.Parts => T(
                $"“{input.Name}”: digest, plus "
                    + (input.Chunks.Count == 1 ? "part " : "parts ")
                    + string.Join(", ", input.Chunks.Order().Select(o => (o + 1).ToString(CultureInfo.InvariantCulture)))
                    + $" of {input.ChunkCount}",
                $"《{input.Name}》：摘要，以及全文 {input.ChunkCount} 部分中的第 {ResourceMaterial.PartList(input.Chunks)} 部分"),
            ResourceForm.Summary => T($"“{input.Name}”: digest only", $"《{input.Name}》：只有摘要"),
            ResourceForm.Whole => T($"“{input.Name}”: full text", $"《{input.Name}》：全文"),
            ResourceForm.Image => T($"“{input.Name}”: image", $"《{input.Name}》：图片"),
            ResourceForm.ImageOmitted => T($"“{input.Name}”: earlier image, not sent again", $"《{input.Name}》：较早的图片，未再发送"),
            ResourceForm.ImageUnsupported => T($"“{input.Name}”: image; the current model can’t view images, so a text description was sent instead", $"《{input.Name}》：图片，当前模型不能查看，以文字说明代替"),
            _ => T($"“{input.Name}”: original missing; a text description was sent instead", $"《{input.Name}》：原件缺失，以文字说明代替"),
        };
    }

    /// <summary>The name of a pasted text that has none of its own (shown on its chip and in the viewer).</summary>
    public static string PastedTextName => T("Pasted text", "粘贴的文字");

    /// <summary>The name of a pasted image that came without a file name.</summary>
    public static string PastedImageName => T("image.png", "图片.png");

    /// <summary>What a screen reader says for an attachment chip.</summary>
    public static string AttachmentAccessibleName(string name) => T($"Attachment: {name}", $"附件：{name}");
}
