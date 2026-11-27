using System.Globalization;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;

namespace Mindweave.Core.Tests;

/// <summary>
/// Phrase sets for the Core tests, which cannot see Application's <c>ModelPhrases</c>. <see cref="Chinese"/> is the text Core
/// built before it took phrases from its caller, so the tests keep their expectations; <see cref="English"/> shows that
/// every piece of model-facing text Core builds comes from the set it is given.
/// </summary>
internal static class TestPhrases
{
    public static IModelPhrases Chinese { get; } = new ChineseSet();

    public static IModelPhrases English { get; } = new EnglishSet();

    private sealed class ChineseSet : IModelPhrases
    {
        public string PastedText => "粘贴的文字";

        public string AttachmentMarker(string name) => $"[附件：{name}]";

        public string AttachmentNotIncluded => "[一个附件，未包含]";

        public string MaterialWhole(string name) => $"以下是用户附上的材料《{name}》的全文：";

        public string MaterialDigest(string name) => $"以下是用户附上的材料《{name}》的摘要（全文较长，这里只有摘要）：";

        public string MaterialDigestAndParts(string name, int chunkCount, IReadOnlyList<int> parts) =>
            $"以下是用户附上的材料《{name}》的摘要，以及其中与问题最相关的部分（全文共 {chunkCount} 部分，这里有第 {string.Join('、', parts)} 部分）。";

        public string DigestHeading => "摘要：";

        public string ChunkHeading(int number) => $"[第 {number} 部分]";

        public string ImageUnviewable(string name) => $"[图片：{name}，当前模型不能查看]";

        public string ImageNotResent(string name) => $"[图片：{name}，较早的图片，因篇幅未再发送]";

        public string Image(string name) => $"[图片：{name}]";

        public string CitationLabel(CitationKind kind, string title) => kind switch
        {
            CitationKind.Attachment => $"附件《{title}》",
            CitationKind.Material => $"材料《{title}》",
            CitationKind.Summary => $"总结《{title}》",
            _ => title,
        };

        public string CitationsHeading => "出处：";

        public string EarlierSummarized(int first, int last) => $"以下是较早一段对话（第 {first}–{last} 条消息）的摘要，原文保存在本地：";

        public string QuotedQuestion(string quoted, string question) => $"关于上文中的这段内容：\n{quoted}\n\n{question}";
    }

    private sealed class EnglishSet : IModelPhrases
    {
        public string PastedText => "Pasted text";

        public string AttachmentMarker(string name) => $"[Attachment: {name}]";

        public string AttachmentNotIncluded => "[An attachment, not included]";

        public string MaterialWhole(string name) => $"Full text of \"{name}\":";

        public string MaterialDigest(string name) => $"Digest of \"{name}\":";

        public string MaterialDigestAndParts(string name, int chunkCount, IReadOnlyList<int> parts) =>
            $"Digest of \"{name}\" and parts {string.Join(", ", parts.Select(p => p.ToString(CultureInfo.InvariantCulture)))} of {chunkCount}.";

        public string DigestHeading => "Digest:";

        public string ChunkHeading(int number) => $"[Part {number}]";

        public string ImageUnviewable(string name) => $"[Image: {name}, not viewable]";

        public string ImageNotResent(string name) => $"[Image: {name}, not sent again]";

        public string Image(string name) => $"[Image: {name}]";

        public string CitationLabel(CitationKind kind, string title) => kind switch
        {
            CitationKind.Attachment when title.Length == 0 => "An attachment",
            CitationKind.Attachment => $"Attachment \"{title}\"",
            CitationKind.Material => $"Material \"{title}\"",
            CitationKind.Summary => $"Summary \"{title}\"",
            _ => title,
        };

        public string CitationsHeading => "Citations:";

        public string EarlierSummarized(int first, int last) => $"Summary of messages {first}–{last}:";

        public string QuotedQuestion(string quoted, string question) => $"About this passage:\n{quoted}\n\n{question}";
    }
}
