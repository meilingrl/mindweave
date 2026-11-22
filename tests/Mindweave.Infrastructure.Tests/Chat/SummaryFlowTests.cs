using Mindweave.Application.Ai;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// S4 through the real coordinator and stores: requirement §11 "summarize midway, keep chatting", "summary fails then
/// retried", "edited summary regenerated", the reliability row on interrupted summary tasks, and same-name naming.
/// </summary>
public class SummaryFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_summary_midway_covers_what_existed_and_later_messages_are_not_pretended_to_be_in_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？", "谁造了火箭号？");

        var summary = await SummarizeAsync(h, conversation);
        await h.AskAsync(conversation, "后来呢？");

        var entry = (await h.SummaryService.ForConversationAsync(conversation, Ct))!;
        Assert.Equal(3, entry.Current.ToSeq);
        Assert.Equal(FakeChatClient.DefaultSummaryTitle, entry.Node.DisplayName);
        Assert.Equal(new SummaryMarkers(Unread: true, HasCandidate: false, HasLaterContent: true), entry.Markers);
        Assert.Equal(summary.Node, entry.Node.Id);
        Assert.Equal(6, (await h.Conversations.LoadAsync(conversation, Ct))!.Messages.Count);

        // The request held only this conversation, its coverage and the instruction.
        var request = h.Client.Requests.Single(FakeChatClient.IsKnowledgeSummary);
        Assert.Equal(["火车是怎么发明的？", "答：火车是怎么发明的？", "谁造了火箭号？", "答：谁造了火箭号？", "请按要求总结以上对话。"], request.Turns.Where(t => t.Role != ChatRole.System).Select(t => t.Content));
        Assert.Equal(SummaryService.SummaryOutputTokens, request.Model.MaxOutputTokens);
    }

    [Fact]
    public async Task A_branch_summary_gets_its_source_as_background_not_as_content_to_summarize()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await ConversationAsync(h, "火车是怎么发明的？");
        var source = (await h.Conversations.LoadAsync(root, Ct))!.Messages[1];
        var branch = await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(source, "火车"), Ct);
        await h.AskAsync(branch.Child, "谁造了第一台？");

        await SummarizeAsync(h, branch.Child);

        var request = h.Client.Requests.Single(FakeChatClient.IsKnowledgeSummary);
        var background = request.Turns.Where(t => t.Role == ChatRole.System).Last().Content;
        Assert.Contains("不要总结", background, StringComparison.Ordinal);
        Assert.Contains("答：火车是怎么发明的？", background, StringComparison.Ordinal);
        Assert.DoesNotContain(request.Turns, t => t.Role != ChatRole.System && t.Content == "火车是怎么发明的？");
    }

    [Fact]
    public async Task Summarizing_is_refused_until_answers_finish_and_while_a_summary_or_candidate_is_pending()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");

        var held = h.Client.Hold("部分");
        var asked = await h.Conversations.AskAsync(conversation, "继续", Ct);
        await held.Started.Task;
        Assert.Equal(SummarizeRejection.AnswerInProgress, (await h.SummaryService.SummarizeAsync(conversation, Ct)).Rejection);
        held.Release.TrySetResult();
        await h.WaitForAnswerAsync(asked.Answer!.Value);

        h.Client.Knowledge.Gate = new TaskCompletionSource();
        var first = await h.SummaryService.SummarizeAsync(conversation, Ct);
        Assert.True(first.Accepted);
        Assert.Equal(SummarizeRejection.SummaryInProgress, (await h.SummaryService.SummarizeAsync(conversation, Ct)).Rejection);

        // A question may still be asked while the summary runs; its coverage was frozen at acceptance.
        await h.AskAsync(conversation, "再问一个");
        h.Client.Knowledge.Gate.TrySetResult();
        var node = await NodeAsync(h, conversation);
        Assert.Equal(3, (await h.SummaryService.GetAsync(node, Ct))!.Current.ToSeq);

        Assert.True((await h.SummaryService.SummarizeAsync(conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.GetAsync(node, Ct))!.Pending is not null);
        Assert.Equal(SummarizeRejection.HasCandidate, (await h.SummaryService.SummarizeAsync(conversation, Ct)).Rejection);
    }

    [Fact]
    public async Task A_conversation_without_a_finished_answer_has_nothing_to_summarize()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Fail(GenerationErrorKind.Network);
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(SummarizeRejection.NothingToSummarize, (await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Rejection);
    }

    [Fact]
    public async Task A_cut_off_summary_saves_nothing_shows_why_and_a_retry_saves_exactly_one()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        h.Client.Knowledge.TruncateNext();

        var failed = await h.SummaryService.SummarizeAsync(conversation, Ct);
        await UntilTaskAsync(h, conversation, GenerationTaskState.Failed);

        Assert.Null(await h.SummaryService.ForConversationAsync(conversation, Ct));
        var status = (await h.SummaryService.LatestTaskAsync(conversation, Ct))!;
        Assert.Equal((failed.Task!.Value, GenerationErrorKind.InvalidResponse), (status.Id, status.Error!.Value));
        Assert.Contains("截断", status.Detail, StringComparison.Ordinal);

        await SummarizeAsync(h, conversation);
        Assert.Equal(1, await h.CountAsync("summary_version"));
    }

    [Fact]
    public async Task Output_without_a_title_and_body_is_not_saved()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        h.Client.Knowledge.Replies.Enqueue("标题：只有标题");

        await h.SummaryService.SummarizeAsync(conversation, Ct);
        await UntilTaskAsync(h, conversation, GenerationTaskState.Failed);

        Assert.Equal(0, await h.CountAsync("summary_node"));
    }

    [Fact]
    public async Task Stopping_a_summary_keeps_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        h.Client.Knowledge.Gate = new TaskCompletionSource();
        var started = await h.SummaryService.SummarizeAsync(conversation, Ct);
        await UntilTaskAsync(h, conversation, GenerationTaskState.Running);

        await h.SummaryService.StopAsync(started.Task!.Value);

        Assert.Equal(GenerationTaskState.Stopped, (await h.SummaryService.LatestTaskAsync(conversation, Ct))!.State);
        Assert.Equal(0, await h.CountAsync("summary_version"));
    }

    [Fact]
    public async Task A_summary_interrupted_by_a_restart_leaves_no_version_and_is_not_sent_again()
    {
        var dir = new TempDataDirectory();
        ConversationId conversation;
        await using (var h = await ChatHarness.StartAsync(directory: dir))
        {
            conversation = await ConversationAsync(h, "火车是怎么发明的？");
            h.Client.Knowledge.Gate = new TaskCompletionSource();
            await h.SummaryService.SummarizeAsync(conversation, Ct);
            await UntilTaskAsync(h, conversation, GenerationTaskState.Running);
        }

        await using var restarted = await ChatHarness.StartAsync(directory: dir);

        // As if the process had ended mid-summary instead of shutting down: the task row is still "running".
        await restarted.Database.WriteAsync((c, t) => Sqlite.SqliteCommands.Execute(c, t, "UPDATE generation_task SET state = 'running' WHERE kind = 'summary';"), Ct);
        await restarted.Store.RecoverUnfinishedAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Equal(GenerationTaskState.Interrupted, (await restarted.SummaryService.LatestTaskAsync(conversation, Ct))!.State);
        Assert.Equal(0, await restarted.CountAsync("summary_version"));
        Assert.DoesNotContain(restarted.Client.Requests, FakeChatClient.IsKnowledgeSummary);
        dir.Dispose();
    }

    [Fact]
    public async Task Regenerating_after_an_edit_keeps_the_edit_current_and_asks_before_replacing_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        var first = await SummarizeAsync(h, conversation);
        var edited = await h.SummaryService.SaveEditAsync(first.Node, first.Version, "蒸汽机车", "我自己的笔记", Ct);

        h.Client.Knowledge.Replies.Enqueue("标题：蒸汽机车\n\n新生成的正文");
        Assert.True((await h.SummaryService.SummarizeAsync(conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.GetAsync(first.Node, Ct))!.Pending is not null);

        var detail = (await h.SummaryService.GetAsync(first.Node, Ct))!;
        Assert.Equal(("我自己的笔记", SummaryOrigin.Edited), (detail.Current.Body, detail.Current.Origin));
        Assert.Equal("新生成的正文", detail.Pending!.Body);

        // The candidate was requested while the edit was already current, so adopting replaces exactly what it saw.
        var check = (await h.SummaryService.CheckAdoptAsync(first.Node, Ct))!;
        Assert.False(check.BaselineChanged);
        Assert.Equal(edited, detail.Pending.Baseline);
    }

    [Fact]
    public async Task A_candidate_requested_before_an_edit_needs_confirmation_to_replace_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        var first = await SummarizeAsync(h, conversation);
        h.Client.Knowledge.Gate = new TaskCompletionSource();
        Assert.True((await h.SummaryService.SummarizeAsync(conversation, Ct)).Accepted);
        await UntilTaskAsync(h, conversation, GenerationTaskState.Running);

        await h.SummaryService.SaveEditAsync(first.Node, first.Version, "蒸汽机车", "生成期间的手改", Ct);
        h.Client.Knowledge.Gate.TrySetResult();
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.GetAsync(first.Node, Ct))!.Pending is not null);

        Assert.True((await h.SummaryService.CheckAdoptAsync(first.Node, Ct))!.BaselineChanged);
    }

    [Fact]
    public async Task Same_name_summaries_get_distinguishing_names_without_touching_manual_names_or_read_state()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = await ConversationAsync(h, "蒸汽机原理？");
        var b = await ConversationAsync(h, "蒸汽机局限？");
        var first = await SummarizeAsync(h, a);
        await h.SummaryNodes.SetReadAsync(first.Version, ReadSource.Manual, DateTimeOffset.UtcNow, Ct);

        h.Client.Naming.Replies.Enqueue("1：原理\n2：局限");
        await SummarizeAsync(h, b);

        await ChatHarness.UntilAsync(async () => (await h.SummaryService.ListAsync(h.Workspace, Ct)).All(e => e.Node.NameSource == NameSource.Auto));
        var names = (await h.SummaryService.ListAsync(h.Workspace, Ct)).Select(e => e.Node.DisplayName).ToList();
        Assert.Equal(["蒸汽机车－原理", "蒸汽机车－局限"], names);
        Assert.False((await h.SummaryService.ForConversationAsync(a, Ct))!.Markers.Unread);
        Assert.Equal(2, await h.CountAsync("summary_version"));

        // A third same-name summary renames the group again; the manual name stays.
        await h.SummaryService.RenameAsync(first.Node, "我的原理笔记", Ct);
        var c = await ConversationAsync(h, "蒸汽机市场？");
        h.Client.Naming.Replies.Enqueue("1：原理\n2：局限\n3：市场");
        await SummarizeAsync(h, c);
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.ForConversationAsync(c, Ct))!.Node.NameSource == NameSource.Auto);

        Assert.Equal("我的原理笔记", (await h.SummaryService.ForConversationAsync(a, Ct))!.Node.DisplayName);
        Assert.Equal("蒸汽机车－市场", (await h.SummaryService.ForConversationAsync(c, Ct))!.Node.DisplayName);
    }

    /// <summary>An English distinguishing term is measured in words (review finding 2026-11-22), not dropped past 16 characters.</summary>
    [Fact]
    public async Task English_distinguishing_terms_are_kept()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = await ConversationAsync(h, "蒸汽机原理？");
        var b = await ConversationAsync(h, "蒸汽机局限？");
        await SummarizeAsync(h, a);

        h.Client.Naming.Replies.Enqueue("1: Thermodynamic efficiency\n2: Practical limits of early designs");
        await SummarizeAsync(h, b);

        await ChatHarness.UntilAsync(async () => (await h.SummaryService.ListAsync(h.Workspace, Ct)).All(e => e.Node.NameSource == NameSource.Auto));
        var names = (await h.SummaryService.ListAsync(h.Workspace, Ct)).Select(e => e.Node.DisplayName).ToList();
        Assert.Equal(["蒸汽机车－Thermodynamic efficiency", "蒸汽机车－Practical limits of early designs"], names);
    }

    [Fact]
    public async Task A_late_naming_result_is_dropped_for_a_node_that_changed_meanwhile()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = await ConversationAsync(h, "蒸汽机原理？");
        var b = await ConversationAsync(h, "蒸汽机局限？");
        await SummarizeAsync(h, a);
        h.Client.Naming.Gate = new TaskCompletionSource();
        await SummarizeAsync(h, b);
        await ChatHarness.UntilAsync(() => Task.FromResult(h.Client.Requests.Any(FakeChatClient.IsNaming)));

        await h.SummaryService.RenameAsync((await h.SummaryService.ForConversationAsync(a, Ct))!.Node.Id, "改过的名字", Ct);
        h.Client.Naming.Gate.TrySetResult();
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.ForConversationAsync(b, Ct))!.Node.NameSource == NameSource.Auto);

        Assert.Equal("改过的名字", (await h.SummaryService.ForConversationAsync(a, Ct))!.Node.DisplayName);
        Assert.Equal("蒸汽机车－区分2", (await h.SummaryService.ForConversationAsync(b, Ct))!.Node.DisplayName);
    }

    [Fact]
    public async Task Reading_marks_a_version_read_only_past_the_threshold_after_enough_visible_time()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        var first = await SummarizeAsync(h, conversation);

        Assert.False(await h.SummaryService.RecordProgressAsync(first.Node, first.Version, 1.0, null, TimeSpan.FromSeconds(1), Ct));
        Assert.False(await h.SummaryService.RecordProgressAsync(first.Node, first.Version, 0.5, null, TimeSpan.FromSeconds(30), Ct));
        Assert.True((await h.SummaryService.ForConversationAsync(conversation, Ct))!.Markers.Unread);

        Assert.True(await h.SummaryService.RecordProgressAsync(first.Node, first.Version, 0.92, null, TimeSpan.FromSeconds(9), Ct));
        Assert.False((await h.SummaryService.ForConversationAsync(conversation, Ct))!.Markers.Unread);

        await h.SummaryService.SetReadAsync(first.Node, first.Version, read: false, Ct);
        Assert.True((await h.SummaryService.ForConversationAsync(conversation, Ct))!.Markers.Unread);
    }

    [Fact]
    public async Task Adopting_a_candidate_with_a_new_title_renames_the_node_unless_named_by_hand()
    {
        await using var h = await ChatHarness.StartAsync();
        var conversation = await ConversationAsync(h, "火车是怎么发明的？");
        var first = await SummarizeAsync(h, conversation);
        h.Client.Knowledge.Replies.Enqueue("标题：铁路的起源\n\n正文");
        Assert.True((await h.SummaryService.SummarizeAsync(conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => (await h.SummaryService.GetAsync(first.Node, Ct))!.Pending is not null);

        var candidate = (await h.SummaryService.GetAsync(first.Node, Ct))!.Pending!.Id;
        await h.SummaryService.AdoptAsync(first.Node, candidate, Ct);

        var node = (await h.SummaryService.GetAsync(first.Node, Ct))!.Node;
        Assert.Equal(("铁路的起源", "铁路的起源", NameSource.Base), (node.BaseTitle, node.DisplayName, node.NameSource));
    }

    private static async Task<SummarySaved> SummarizeAsync(ChatHarness h, ConversationId conversation)
    {
        var before = (await h.SummaryService.ForConversationAsync(conversation, Ct))?.Node.Current;
        var outcome = await h.SummaryService.SummarizeAsync(conversation, Ct);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await UntilTaskAsync(h, conversation, GenerationTaskState.Completed, outcome.Task);
        var entry = (await h.SummaryService.ForConversationAsync(conversation, Ct))!;
        return new SummarySaved(entry.Node.Id, entry.Node.Pending ?? entry.Node.Current, before is not null);
    }

    private static async Task<SummaryNodeId> NodeAsync(ChatHarness h, ConversationId conversation)
    {
        await ChatHarness.UntilAsync(async () => await h.SummaryService.ForConversationAsync(conversation, Ct) is not null);
        return (await h.SummaryService.ForConversationAsync(conversation, Ct))!.Node.Id;
    }

    private static Task UntilTaskAsync(ChatHarness h, ConversationId conversation, GenerationTaskState state, GenerationTaskId? task = null) =>
        ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(conversation, Ct) is { } s && s.State == state && (task is null || s.Id == task));

    private static async Task<ConversationId> ConversationAsync(ChatHarness h, string first, params string[] more)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, first, Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        foreach (var question in more)
        {
            await h.AskAsync(sent.Conversation, question);
        }

        return sent.Conversation;
    }
}
