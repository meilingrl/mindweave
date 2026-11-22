using Mindweave.Application.Languages;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.Tests.Chat;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Languages;

/// <summary>
/// The short text Core puts into requests comes from Application's phrase sets (docs/design/bilingual-ui.md §5, plan
/// Task 15): the Chinese set is what Core wrote before, byte for byte; the English set has no Chinese and keeps every
/// argument; requests are built in the interface language of the moment.
/// </summary>
[Collection(LanguageTests.Name)]
public class ModelPhrasesTests
{
    private static readonly ResourceId Id = ResourceId.Parse("0190f000-0000-7000-8000-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Chinese_phrases_are_what_Core_wrote_before()
    {
        // Captured from Core at 95be451, where the text was written in place.
        var p = ModelPhrases.Chinese;
        var notes = new AttachmentBlock(Id, "notes.md", "text/markdown", 10, "# 标题\n正文\n");
        var paste = new AttachmentBlock(Id, null, "text/plain", 6, "内容");
        var image = new AttachmentBlock(Id, "a.png", "image/png", 6, null);
        var conversation = ConversationId.New();

        Assert.Equal("粘贴的文字", p.PastedText);
        Assert.Equal("[附件：粘贴的文字]\n\n总结一下", ContentBlocks.TextOf([paste, new TextBlock("总结一下")], p));
        Assert.Equal("<attachment name=\"notes.md\" type=\"text/markdown\" form=\"whole\">\n以下是用户附上的材料《notes.md》的全文：\n\n# 标题\n正文\n</attachment>", ResourceMaterial.Whole(notes, p));
        Assert.Equal("<attachment name=\"notes.md\" type=\"text/markdown\" form=\"summary\">\n以下是用户附上的材料《notes.md》的摘要（全文较长，这里只有摘要）：\n\n要点\n</attachment>", ResourceMaterial.Summary(notes, "要点", p));
        Assert.Equal(
            "<attachment name=\"notes.md\" type=\"text/markdown\" form=\"parts\">\n以下是用户附上的材料《notes.md》的摘要，以及其中与问题最相关的部分（全文共 12 部分，这里有第 3、11 部分）。\n\n摘要：\n要点\n\n[第 3 部分]\n丙\n\n[第 11 部分]\n壬\n</attachment>",
            ResourceMaterial.Parts(notes, "要点", [(10, "壬"), (2, "丙")], 12, p));
        Assert.Equal("[第 1 部分]", ResourceMaterial.ChunkHeading(0, p));
        Assert.Equal("[图片：a.png，当前模型不能查看]", ResourceMaterial.ImageUnsupported(image, p));
        Assert.Equal("[图片：a.png，较早的图片，因篇幅未再发送]", ResourceMaterial.ImageOmitted(image, p));
        Assert.Equal("[图片：a.png]", ResourceMaterial.ImageLabel(image, p));
        Assert.Equal(
            "答 [1][2][3][4]\n\n出处：\n[1] 蒸汽机 https://example.com/steam\n[2] 附件《notes.md》\n[3] 材料《旧网页》\n[4] 总结《瓦特》",
            ContentBlocks.ContextTextOf([new TextBlock("答 [1][2][3][4]"), .. Citations()], p));
        Assert.Equal("关于上文中的这段内容：\n> 第一行\n> 第二行\n\n问", BranchContext.QuotedQuestion("第一行\r\n第二行", "问", p));
        Assert.Equal("以下是较早一段对话（第 3–8 条消息）的摘要，原文保存在本地：\n\n摘要", ContextPlanner.SummaryMessage(new SummaryKey(conversation, 2, 7, "h", 1), "摘要", p));
    }

    [Fact]
    public void English_phrases_have_no_Chinese_and_keep_every_argument()
    {
        foreach (var (member, english, chinese) in Rendered())
        {
            Assert.False(HasChinese(english), $"{member}: {english}");
            foreach (var argument in new[] { "NAME", "TITLE", "QUOTED", "QUESTION" }.Where(chinese.Contains))
            {
                Assert.Contains(argument, english, StringComparison.Ordinal);
            }

            foreach (var number in new[] { "3", "7", "11", "12" }.Where(n => chinese.Contains(n, StringComparison.Ordinal)))
            {
                Assert.Contains(number, english, StringComparison.Ordinal);
            }
        }

        Assert.Equal("[Attachment: notes.md]", ModelPhrases.English.AttachmentMarker("notes.md"));
        Assert.Equal("Steam", ModelPhrases.English.CitationLabel(CitationKind.Web, "Steam"));
        Assert.Contains("the full text has 1 part; here is part 1)", ModelPhrases.English.MaterialDigestAndParts("x", 1, [1]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_parts_opening_fits_its_allowance_in_either_language()
    {
        // ResourceLayer plans the digest-and-parts form at the digest-only form's cost plus PartsOverheadTokens.
        foreach (var phrases in new[] { ModelPhrases.English, ModelPhrases.Chinese })
        {
            var digestOnly = TokenEstimator.Estimate(phrases.MaterialDigest("notes.md") + "\n\n");
            var parts = TokenEstimator.Estimate(phrases.MaterialDigestAndParts("notes.md", 120, [3, 17, 45, 88, 119]) + "\n\n" + phrases.DigestHeading + "\n");
            Assert.InRange(parts - digestOnly, 0, ResourceMaterial.PartsOverheadTokens);
        }
    }

    [Fact]
    public void Current_follows_the_language()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            Assert.Same(ModelPhrases.English, ModelPhrases.Current);
            CurrentLanguage.Set(UiLanguage.Chinese);
            Assert.Same(ModelPhrases.Chinese, ModelPhrases.Current);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    [Fact]
    public async Task A_request_is_built_in_the_interface_language()
    {
        try
        {
            CurrentLanguage.Set(UiLanguage.English);
            await using var h = await ChatHarness.StartAsync();
            var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), "first\r\nsecond", Ct)).Resource!;

            var sent = await h.Conversations.StartAsync(h.Workspace, "summarize", [paste.Resource.Id], null, Ct);
            Assert.True(sent.Accepted, sent.Rejection?.ToString());
            await h.WaitForAnswerAsync(sent.Answer!.Value);

            var question = h.Client.Requests.Single(FakeChatClient.IsAnswer).Turns[^1].Content;
            Assert.StartsWith(
                "<attachment name=\"Pasted text\" type=\"text/plain\" form=\"whole\">\nBelow is the full text of the material \"Pasted text\" that the user attached:\n\nfirst\nsecond\n</attachment>",
                question,
                StringComparison.Ordinal);
            Assert.EndsWith("[Attachment: Pasted text]\n\nsummarize", question, StringComparison.Ordinal);
        }
        finally
        {
            CurrentLanguage.Set(UiLanguage.Chinese);
        }
    }

    private static IEnumerable<CitationBlock> Citations() =>
    [
        new(1, "蒸汽机", new Uri("https://example.com/steam"), DateTimeOffset.UnixEpoch, Id, 0, 1),
        new(2, "notes.md", null, DateTimeOffset.UnixEpoch, Id, 0, 1) { Kind = CitationKind.Attachment },
        new(3, "旧网页", null, DateTimeOffset.UnixEpoch, Id, 0, 1) { Kind = CitationKind.Material },
        new(4, "瓦特", null, DateTimeOffset.UnixEpoch, Id, 0, 1) { Kind = CitationKind.Summary },
    ];

    /// <summary>Every member of both sets with the same distinctive arguments.</summary>
    private static IEnumerable<(string Member, string English, string Chinese)> Rendered()
    {
        static IEnumerable<(string, Func<IModelPhrases, string>)> Members() =>
        [
            ("PastedText", p => p.PastedText),
            ("AttachmentMarker", p => p.AttachmentMarker("NAME")),
            ("MaterialWhole", p => p.MaterialWhole("NAME")),
            ("MaterialDigest", p => p.MaterialDigest("NAME")),
            ("MaterialDigestAndParts", p => p.MaterialDigestAndParts("NAME", 12, [3, 7, 11])),
            ("DigestHeading", p => p.DigestHeading),
            ("ChunkHeading", p => p.ChunkHeading(7)),
            ("ImageUnviewable", p => p.ImageUnviewable("NAME")),
            ("ImageNotResent", p => p.ImageNotResent("NAME")),
            ("Image", p => p.Image("NAME")),
            ("CitationLabel.Attachment", p => p.CitationLabel(CitationKind.Attachment, "TITLE")),
            ("CitationLabel.Material", p => p.CitationLabel(CitationKind.Material, "TITLE")),
            ("CitationLabel.Summary", p => p.CitationLabel(CitationKind.Summary, "TITLE")),
            ("CitationLabel.Web", p => p.CitationLabel(CitationKind.Web, "TITLE")),
            ("CitationsHeading", p => p.CitationsHeading),
            ("EarlierSummarized", p => p.EarlierSummarized(3, 12)),
            ("QuotedQuestion", p => p.QuotedQuestion("QUOTED", "QUESTION")),
        ];

        return Members().Select(m => (m.Item1, m.Item2(ModelPhrases.English), m.Item2(ModelPhrases.Chinese)));
    }

    private static bool HasChinese(string text) => text.Any(c => c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯');
}
