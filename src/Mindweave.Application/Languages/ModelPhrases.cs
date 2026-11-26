using System.Globalization;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Languages;

/// <summary>
/// The short text Core puts into requests (docs/design/bilingual-ui.md §5), in each language side by side. Callers pass
/// <see cref="Current"/> when they build a request, so a request is in the interface language at that moment. The Chinese
/// phrases are what Core wrote before it took them from its caller, byte for byte.
/// </summary>
public sealed class ModelPhrases : IModelPhrases
{
    private readonly UiLanguage _language;

    private ModelPhrases(UiLanguage language) => _language = language;

    public static IModelPhrases English { get; } = new ModelPhrases(UiLanguage.English);

    public static IModelPhrases Chinese { get; } = new ModelPhrases(UiLanguage.Chinese);

    /// <summary>The phrases of the current interface language.</summary>
    public static IModelPhrases Current => For(CurrentLanguage.Value);

    public static IModelPhrases For(UiLanguage language) => language == UiLanguage.English ? English : Chinese;

    public string PastedText => Pick("Pasted text", "粘贴的文字");

    public string AttachmentMarker(string name) => Pick($"[Attachment: {name}]", $"[附件：{name}]");

    public string AttachmentNotIncluded => Pick("[An attachment was here; it is not included]", "[这里有一个附件，未包含在内]");

    public string MaterialWhole(string name) =>
        Pick($"Below is the full text of the material \"{name}\" that the user attached:", $"以下是用户附上的材料《{name}》的全文：");

    public string MaterialDigest(string name) => Pick(
        $"Below is the digest of the material \"{name}\" that the user attached (the full text is long; only the digest is here):",
        $"以下是用户附上的材料《{name}》的摘要（全文较长，这里只有摘要）：");

    public string MaterialDigestAndParts(string name, int chunkCount, IReadOnlyList<int> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var numbers = parts.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList();
        return Pick(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Below is the digest of the material \"{name}\" that the user attached, and the parts of it most relevant to the question (the full text has {Bilingual.Plural(chunkCount, "part", "parts")}; here {(numbers.Count == 1 ? "is part" : "are parts")} {string.Join(", ", numbers)})."),
            string.Create(
                CultureInfo.InvariantCulture,
                $"以下是用户附上的材料《{name}》的摘要，以及其中与问题最相关的部分（全文共 {chunkCount} 部分，这里有第 {string.Join('、', numbers)} 部分）。"));
    }

    public string DigestHeading => Pick("Digest:", "摘要：");

    public string ChunkHeading(int number) =>
        Pick(string.Create(CultureInfo.InvariantCulture, $"[Part {number}]"), string.Create(CultureInfo.InvariantCulture, $"[第 {number} 部分]"));

    public string ImageUnviewable(string name) =>
        Pick($"[Image: {name}; the current model can't view images]", $"[图片：{name}，当前模型不能查看]");

    public string ImageNotResent(string name) =>
        Pick($"[Image: {name}; an earlier image, not sent again for lack of room]", $"[图片：{name}，较早的图片，因篇幅未再发送]");

    public string Image(string name) => Pick($"[Image: {name}]", $"[图片：{name}]");

    public string CitationLabel(CitationKind kind, string title) => kind switch
    {
        CitationKind.Attachment when title.Length == 0 => Pick("An attachment", "一个附件"),
        CitationKind.Material when title.Length == 0 => Pick("Material found earlier", "较早取得的材料"),
        CitationKind.Summary when title.Length == 0 => Pick("A summary", "一个总结"),
        _ when title.Length == 0 => Pick("A web page", "一个网页"),
        CitationKind.Attachment => Pick($"Attachment \"{title}\"", $"附件《{title}》"),
        CitationKind.Material => Pick($"Material \"{title}\"", $"材料《{title}》"),
        CitationKind.Summary => Pick($"Summary \"{title}\"", $"总结《{title}》"),
        _ => title,
    };

    public string CitationsHeading => Pick("Citations:", "出处：");

    public string EarlierSummarized(int first, int last) => Pick(
        string.Create(CultureInfo.InvariantCulture, $"Below is a summary of an earlier part of the conversation (messages {first}–{last}); the original is kept locally:"),
        string.Create(CultureInfo.InvariantCulture, $"以下是较早一段对话（第 {first}–{last} 条消息）的摘要，原文保存在本地："));

    public string QuotedQuestion(string quoted, string question) =>
        Pick($"About this passage from above:\n{quoted}\n\n{question}", $"关于上文中的这段内容：\n{quoted}\n\n{question}");

    private string Pick(string english, string chinese) => _language == UiLanguage.English ? english : chinese;
}
