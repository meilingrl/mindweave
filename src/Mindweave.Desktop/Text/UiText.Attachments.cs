using System.Globalization;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Resources;

namespace Mindweave.Desktop.Text;

// Attachments (requirements §10.4.3, agent platform phase 1): the composer's "+", attachment chips, the viewer, and the
// note under an answer that got only parts or a summary of an attachment.
public static partial class UiText
{
    public const string AttachMenu = "添加附件";
    public const string AttachFile = "文件…";
    public const string AttachImage = "图片…";
    public const string ImagesUnavailable = "当前模型不能查看图片，可在设置 › 模型连接 › 高级设置中声明";
    public const string PastedImageRefused = "当前模型不能查看图片，图片没有附加。可在设置 › 模型连接 › 高级设置中声明。";
    public const string TooManyAttachmentsNotice = "一条提问最多带 10 个附件。";
    public const string RemoveAttachment = "移除";
    public const string ConvertToText = "转为文字";
    public const string RetryAttachment = "重试";
    public const string OpenAttachment = "查看";
    public const string Extracting = "正在读取…";
    public const string OriginalMissing = "原件缺失";
    public const string OriginalMissingNote = "附件的原件已不在数据目录中。已提取的文字仍可查看和发送。";
    public const string ImageOriginalMissingNote = "图片的原件已不在数据目录中，无法显示；发送时以文字说明代替。";
    public const string TruncatedNote = "这份材料提取出的文字超过 200 万字，只保存了前 200 万字。";
    public const string ViewerCopyAll = "复制全部";
    public const string ViewerClose = "关闭";
    public const string DropHint = "松开即可附加";
    public const string HistoryImagesUnseen = "会话中的图片会以文字说明代替发送：当前模型不能查看图片";
    public const string FileDialogTitle = "选择要附加的文件";
    public const string ImageDialogTitle = "选择要附加的图片";
    public const string AttachmentsFileFilter = "文本、PDF 与 Word";
    public const string ImagesFileFilter = "图片";

    /// <summary>Why an attachment could not be added, on its chip (requirements §10.4.3).</summary>
    public static string AttachmentProblem(ResourceCreateStatus status, string? detail = null, bool image = false) => status switch
    {
        ResourceCreateStatus.Unsupported => string.IsNullOrEmpty(detail) || !detail.StartsWith('.') ? "不支持这种文件" : $"不支持 {detail} 文件",
        ResourceCreateStatus.TooLarge => image ? "图片超过 20 MB 或像素过多" : "文件超过 50 MB",
        ResourceCreateStatus.NotReadable => image ? "无法读取这张图片" : "无法读取其中的文字",
        ResourceCreateStatus.NoText => "没有可提取的文字（可能是扫描件）",
        ResourceCreateStatus.Encrypted => "文件有密码保护",
        ResourceCreateStatus.FileUnavailable => "无法打开这个文件",
        ResourceCreateStatus.Empty => "内容是空的",
        _ => "无法附加",
    };

    public const string AttachmentSaveFailed = "保存失败，可能是磁盘已满";

    /// <summary>"约 1.2 万字" or "约 3,400 字" for a text; "1.2 MB" for a file.</summary>
    public static string AttachmentSize(int characters) =>
        characters >= 9_500
            ? $"约 {(characters / 10_000.0).ToString("0.#", CultureInfo.InvariantCulture)} 万字"
            : characters >= 1_000 ? $"约 {((characters + 50) / 100 * 100).ToString("N0", CultureInfo.InvariantCulture)} 字" : $"{characters} 字";

    public static string ByteSize(long bytes) =>
        bytes >= 1_000_000 ? $"{(bytes / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} MB"
        : bytes >= 1_000 ? $"{(bytes / 1_000.0).ToString("0", CultureInfo.InvariantCulture)} KB"
        : $"{bytes} 字节";

    /// <summary>A short type word for a chip.</summary>
    public static string AttachmentType(string mediaType, bool paste) =>
        paste ? "文字"
        : mediaType == MediaTypes.Pdf ? "PDF"
        : mediaType == MediaTypes.Docx ? "Word"
        : MediaTypes.IsImage(mediaType) ? "图片"
        : mediaType == MediaTypes.Markdown ? "Markdown"
        : mediaType is "text/csv" or "text/tab-separated-values" ? "表格文字"
        : mediaType is MediaTypes.PlainText ? "文本"
        : "代码";

    // The note under an answer whose request sent only parts or a summary of an attachment (§10.4.3, item 3 of 发送什么).
    public const string AttachmentReducedNote = "附件较长，只发送了与问题相关的部分";
    public const string AttachmentSummaryNote = "较早的附件只发送了摘要";
    public const string AttachmentReducedDetails = "发送了哪些";

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
            ResourceForm.Parts => $"《{input.Name}》：摘要，以及全文 {input.ChunkCount} 部分中的第 {ResourceMaterial.PartList(input.Chunks)} 部分",
            ResourceForm.Summary => $"《{input.Name}》：只有摘要",
            ResourceForm.Whole => $"《{input.Name}》：全文",
            ResourceForm.Image => $"《{input.Name}》：图片",
            ResourceForm.ImageOmitted => $"《{input.Name}》：较早的图片，未再发送",
            ResourceForm.ImageUnsupported => $"《{input.Name}》：图片，当前模型不能查看，以文字说明代替",
            _ => $"《{input.Name}》：原件缺失，以文字说明代替",
        };
    }
}
