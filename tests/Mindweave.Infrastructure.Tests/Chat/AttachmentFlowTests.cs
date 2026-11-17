using Microsoft.Data.Sqlite;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Phase 1 acceptance (requirements §11, agent platform phase 1; architecture §15.4) through the real store, coordinator and
/// fake provider: what an answer request sends for attachments, summaries made once, refusal instead of silent cutting,
/// images by capability, drafts across a restart, branches, a missing original.
/// </summary>
public class AttachmentFlowTests
{
    /// <summary>Budget 12,000 − 4,096 − 1,200 = 6,704 estimated tokens.</summary>
    private static readonly ModelSettingsInput SmallModel = new("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 12_000);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An article of about 20,000 estimated tokens in paragraphs, the word 蒸汽机 only in paragraph 17.</summary>
    private static readonly string Article = string.Concat(Enumerable.Range(0, 40).Select(i => (i == 17 ? "这一段讲蒸汽机的发明。" : $"第{i}段。") + new string('文', 490) + "\n\n"));

    [Fact]
    public async Task An_attachment_that_fits_is_sent_whole_as_marked_material_and_leaves_the_draft()
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = DraftSlot.NewExploration(h.Workspace);
        var paste = (await h.ResourceService.AttachPasteAsync(draft, "第一段\r\n第二段", Ct)).Resource!;
        Assert.Single(await h.ResourceService.GetDraftAsync(draft, Ct));

        var sent = await h.Conversations.StartAsync(h.Workspace, "总结一下", [paste.Resource.Id], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        var request = h.Client.Requests.Single(FakeChatClient.IsAnswer);
        var question = request.Turns[^1].Content;
        Assert.StartsWith("<attachment name=\"粘贴的文字\" type=\"text/plain\" form=\"whole\">\n以下是用户附上的材料《粘贴的文字》的全文：\n\n第一段\n第二段\n</attachment>", question, StringComparison.Ordinal);
        Assert.EndsWith("[附件：粘贴的文字]\n\n总结一下", question, StringComparison.Ordinal);
        Assert.Empty(await h.ResourceService.GetDraftAsync(draft, Ct));
        Assert.Empty((await h.Conversations.LoadAsync(sent.Conversation, Ct))!.ReducedAttachments!);

        // The question shows its attachment above its text; the title is asked from names only.
        var stored = (await h.Store.GetMessagesAsync(sent.Conversation, Ct))[0];
        Assert.IsType<AttachmentBlock>(stored.Blocks[0]);
        Assert.Equal("总结一下", ContentBlocks.TypedTextOf(stored.Blocks));
    }

    [Fact]
    public async Task A_question_of_only_attachments_is_accepted_and_one_without_either_is_not()
    {
        await using var h = await ChatHarness.StartAsync();
        var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), "材料", Ct)).Resource!;

        Assert.Equal(SendRejection.EmptyQuestion, (await h.Conversations.StartAsync(h.Workspace, " ", [], null, Ct)).Rejection);
        var sent = await h.Conversations.StartAsync(h.Workspace, "", [paste.Resource.Id], null, Ct);
        Assert.True(sent.Accepted);
        Assert.Equal("[附件：粘贴的文字]", (await h.Store.GetMessagesAsync(sent.Conversation, Ct))[0].Text);
    }

    [Fact]
    public async Task An_attachment_over_capacity_is_sent_as_summary_and_relevant_parts_and_the_answer_says_so()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), Article, Ct)).Resource!;

        var sent = await h.Conversations.StartAsync(h.Workspace, "蒸汽机是怎么发明的？", [paste.Resource.Id], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Complete, answer.State);

        // The attachment is longer than the model takes at once: its summary is made from segments, then merged (one stored
        // summary); the answer sends it with the parts the question matched first.
        var summaryRequests = h.Client.Requests.Count(FakeChatClient.IsResourceSummary);
        Assert.True(summaryRequests >= 3, summaryRequests.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1L, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM resource_derivative WHERE kind = 'summary';"), Ct));
        var request = h.Client.Requests.Single(FakeChatClient.IsAnswer);
        var question = request.Turns[^1].Content;
        Assert.Contains("form=\"parts\"", question, StringComparison.Ordinal);
        Assert.Contains("附件摘要：", question, StringComparison.Ordinal);
        Assert.Contains("这一段讲蒸汽机的发明", question, StringComparison.Ordinal);
        Assert.True(ChatTurns.Estimate(request.Turns) <= 6_704);

        var reduced = (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.ReducedAttachments![answer.Id];
        var input = Assert.Single(reduced);
        Assert.Equal(ResourceForm.Parts, input.Form);
        Assert.True(input.ChunkCount > input.Chunks.Count);

        // The summary request is counted for the answer's task under its own purpose.
        var purposes = await h.Database.ReadAsync(c => c.Scalar<string>(null, "SELECT group_concat(purpose, ',') FROM (SELECT purpose FROM ai_request WHERE purpose IS NOT NULL ORDER BY started_at, rowid);"), Ct);
        Assert.Equal(string.Join(',', Enumerable.Repeat("resource_summary", summaryRequests).Append("answer")), purposes);
    }

    [Fact]
    public async Task Later_questions_send_the_older_attachment_as_its_summary_made_only_once()
    {
        await using var h = await ChatHarness.StartAsync(model: SmallModel);
        var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), Article[..14_000], Ct)).Resource!;
        var sent = await h.Conversations.StartAsync(h.Workspace, "讲了什么", [paste.Resource.Id], null, Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var made = h.Client.Requests.Count(FakeChatClient.IsResourceSummary);
        Assert.True(made > 0);

        await h.AskAsync(sent.Conversation, "第二个问题" + new string('问', 2_000));
        await h.AskAsync(sent.Conversation, "第三个问题");

        var answers = h.Client.Requests.Where(FakeChatClient.IsAnswer).ToList();
        Assert.Contains("form=\"summary\"", answers[^1].Turns.First(t => t.Content.Contains("<attachment", StringComparison.Ordinal)).Content, StringComparison.Ordinal);
        Assert.Equal(made, h.Client.Requests.Count(FakeChatClient.IsResourceSummary));
        Assert.Equal((long)made, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM ai_request WHERE purpose = 'resource_summary';"), Ct));
    }

    [Fact]
    public async Task A_question_whose_attachment_cannot_fit_is_refused_and_nothing_is_stored()
    {
        await using var h = await ChatHarness.StartAsync(model: new("fake", "https://fake.example/v1", "fake-model", "sk-test", ContextWindowTokens: 2_000));
        var draft = DraftSlot.NewExploration(h.Workspace);
        var paste = (await h.ResourceService.AttachPasteAsync(draft, Article, Ct)).Resource!;

        var sent = await h.Conversations.StartAsync(h.Workspace, "讲了什么", [paste.Resource.Id], null, Ct);

        Assert.Equal(SendRejection.QuestionTooLargeForModel, sent.Rejection);
        Assert.Single(await h.ResourceService.GetDraftAsync(draft, Ct)); // the attachment stays in the draft
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public async Task Images_go_as_image_parts_only_to_a_model_that_can_see_them()
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = DraftSlot.NewExploration(h.Workspace);
        var image = (await h.ResourceService.AttachImageAsync(draft, Png, null, Ct)).Resource!;
        Assert.Equal(("图片.png", MediaTypes.Png, ResourceOrigin.Image), (image.Resource.Name, image.Resource.MediaType, image.Resource.Origin));

        Assert.Equal(SendRejection.ImagesNotSupported, (await h.Conversations.StartAsync(h.Workspace, "这是什么", [image.Resource.Id], null, Ct)).Rejection);

        var settings = (await h.Models.GetActiveSnapshotAsync(Ct))!;
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", null, null, settings.Capabilities with { ImageInput = true }), Ct);
        var sent = await h.Conversations.StartAsync(h.Workspace, "这是什么", [image.Resource.Id], null, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var turn = h.Client.Requests.Single(FakeChatClient.IsAnswer).Turns[^1];
        var part = Assert.Single(turn.Images!);
        Assert.Equal(MediaTypes.Png, part.MediaType);
        Assert.Equal(Png, part.Data);
        Assert.StartsWith("[图片：图片.png]", turn.Content, StringComparison.Ordinal);

        // The model changes to one that cannot see: the image in the history goes as a line saying so.
        await h.Models.SaveAsync(new ModelSettingsInput("fake", "https://fake.example/v1", "fake-model", null, null, settings.Capabilities with { ImageInput = false }), Ct);
        await h.AskAsync(sent.Conversation, "还记得吗");
        var later = h.Client.Requests.Where(FakeChatClient.IsAnswer).Last();
        Assert.Null(later.Turns[0].Images);
        Assert.StartsWith("[图片：图片.png，当前模型不能查看]", later.Turns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_draft_attachment_survives_a_restart_and_can_be_sent()
    {
        var dir = new TempDataDirectory();
        ConversationId conversation;
        await using (var first = await ChatHarness.StartAsync(directory: dir))
        {
            var (c, _) = await first.RootWithAnswerAsync("第一个问题");
            conversation = c;
            await first.ResourceService.AttachPasteAsync(new DraftSlot(first.Workspace, conversation, "main"), "草稿里的材料", Ct);
        }

        await using var h = await ChatHarness.StartAsync(directory: dir, configure: false);
        var workspace = (await h.Store.GetAsync(conversation, Ct))!.WorkspaceId;
        await h.ResourceService.CleanUpAsync(Ct); // what startup does: a draft attachment is not abandoned
        var draft = await h.ResourceService.GetDraftAsync(new DraftSlot(workspace, conversation, "main"), Ct);
        var kept = Assert.Single(draft);
        Assert.Equal("草稿里的材料", kept.Text);
        var sent = await h.Conversations.AskAsync(conversation, "问", [kept.Resource.Id], Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        dir.Dispose();
    }

    [Fact]
    public async Task A_branch_uses_the_attachment_of_its_frozen_ancestors_and_not_a_siblings()
    {
        await using var h = await ChatHarness.StartAsync();
        var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), "父会话的材料", Ct)).Resource!;
        var sent = await h.Conversations.StartAsync(h.Workspace, "讲讲", [paste.Resource.Id], null, Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        var sibling = await h.BranchAsync(sent.Conversation, answer, "q");
        var siblingPaste = (await h.ResourceService.AttachPasteAsync(new DraftSlot(h.Workspace, sibling, "main"), "兄弟分支的材料", Ct)).Resource!;
        var asked = await h.Conversations.AskAsync(sibling, "兄弟的问题", [siblingPaste.Resource.Id], Ct);
        await h.WaitForAnswerAsync(asked.Answer!.Value);

        var child = await h.BranchAsync(sent.Conversation, answer, "q");
        await h.AskAsync(child, "追问材料");

        var request = h.Client.Requests.Where(FakeChatClient.IsAnswer).Last();
        var all = string.Join("\n", request.Turns.Select(t => t.Content));
        Assert.Contains("父会话的材料", all, StringComparison.Ordinal);
        Assert.DoesNotContain("兄弟分支的材料", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_original_still_sends_the_extracted_text_and_the_conversation_opens()
    {
        await using var h = await ChatHarness.StartAsync();
        var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), "提取过的文字", Ct)).Resource!;
        var sent = await h.Conversations.StartAsync(h.Workspace, "讲讲", [paste.Resource.Id], null, Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        File.Delete(h.Blobs.PathOf(paste.Resource.Hash));

        Assert.False(h.ResourceService.HasOriginal(paste.Resource));
        Assert.NotNull(await h.Conversations.LoadAsync(sent.Conversation, Ct));
        await h.AskAsync(sent.Conversation, "再讲讲");
        Assert.Contains("提取过的文字", h.Client.Requests.Where(FakeChatClient.IsAnswer).Last().Turns[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_knowledge_summary_sees_an_attachment_as_its_name_and_opening_not_its_whole_text()
    {
        await using var h = await ChatHarness.StartAsync();
        var paste = (await h.ResourceService.AttachPasteAsync(DraftSlot.NewExploration(h.Workspace), "开头的话。" + new string('长', 2_000), Ct)).Resource!;
        var sent = await h.Conversations.StartAsync(h.Workspace, "讲讲", [paste.Resource.Id], null, Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsKnowledgeSummary));

        // The question's turn (the fake's answer echoes what it was sent, so only the question is looked at).
        var question = h.Client.Requests.First(FakeChatClient.IsKnowledgeSummary).Turns.Single(t => t.Role == ChatRole.User && t.Content.Contains("[附件：", StringComparison.Ordinal)).Content;
        Assert.StartsWith("[附件：粘贴的文字]（开头）\n开头的话。", question, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('长', 400), question, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_many_attachments_are_refused()
    {
        await using var h = await ChatHarness.StartAsync();
        var draft = DraftSlot.NewExploration(h.Workspace);
        var ids = new List<ResourceId>();
        for (var i = 0; i <= ResourceLimits.MaxAttachmentsPerMessage; i++)
        {
            ids.Add((await h.ResourceService.AttachPasteAsync(draft, $"材料{i}", Ct)).Resource!.Resource.Id);
        }

        Assert.Equal(SendRejection.TooManyAttachments, (await h.Conversations.StartAsync(h.Workspace, "问", ids, null, Ct)).Rejection);
    }

    /// <summary>A real 1×1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
