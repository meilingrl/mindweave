using Mindweave.Application.Ai;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Names suggested for a workspace while renaming it (2026-11-11), through the real coordinator and SQLite with the fake
/// service: what is sent (bounded), how the answer is cleaned, and that the request is stopped, refused or failed without
/// leaving anything stored.
/// </summary>
public class WorkspaceNameSuggestionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WorkspaceName Name(string raw)
    {
        Assert.True(WorkspaceName.TryCreate(raw, out var name, out _));
        return name!;
    }

    [Fact]
    public async Task The_request_sends_the_conversation_and_summary_titles_and_the_current_name_and_stores_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var steam = await StartAsync(h, "蒸汽机是怎么工作的？", "蒸汽机的原理");
        h.Client.Titles.FailNext(GenerationErrorKind.ServerError);
        await StartAsync(h, "铁轨为什么是工字形的？", title: null);
        await h.SummarizeAsync(steam);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        var before = await StoredRowsAsync(h);

        var result = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);

        Assert.Null(result.Problem);
        Assert.Equal(["蒸汽机车史", "铁路发展", "火车与工业革命"], result.Names.Select(n => n.Value));
        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Equal(WorkspaceNamePrompt.OutputTokens, request.Model.MaxOutputTokens);
        Assert.Equal(ChatRole.System, request.Turns[0].Role);
        var sent = request.Turns[1].Content;
        Assert.Contains("当前名称：测试", sent, StringComparison.Ordinal);
        Assert.Contains("- 蒸汽机的原理", sent, StringComparison.Ordinal);

        // An untitled conversation is named by its first question; a summary by the name it shows.
        Assert.Contains("- 铁轨为什么是工字形的？", sent, StringComparison.Ordinal);
        Assert.Contains($"总结标题（共 1 个，列出 1 个）：\n- {FakeChatClient.DefaultSummaryTitle}", sent, StringComparison.Ordinal);

        // Shown only: no task row, no request trace, nothing left running.
        Assert.Equal(before, await StoredRowsAsync(h));
        Assert.Equal(0, h.Generation.UnfinishedCount);
    }

    [Fact]
    public async Task The_input_is_bounded_and_prefers_the_most_recent_root_conversations()
    {
        await using var h = await ChatHarness.StartAsync();
        ConversationId? first = null;
        for (var i = 1; i <= 24; i++)
        {
            var id = await StartAsync(h, $"问题 {i}", $"第{i:00}个话题：" + new string('长', 34));
            first ??= id;
        }

        // A branch is the most recent of all, yet roots come first; with this many roots it is not sent at all.
        var answer = (await h.Conversations.LoadAsync(first!.Value, Ct))!.Messages[^1];
        var branch = await h.BranchAsync(first.Value, answer);
        await h.Conversations.RenameAsync(branch, "分支里的问题", Ct);

        var result = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);

        Assert.Null(result.Problem);
        var sent = Assert.Single(h.Client.Requests, FakeChatClient.IsWorkspaceNaming).Turns[1].Content;
        var titles = sent.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();
        Assert.Equal(WorkspaceNamePrompt.MaxConversations, titles.Count);
        Assert.StartsWith("第24个话题", titles[0], StringComparison.Ordinal);
        Assert.StartsWith("第05个话题", titles[^1], StringComparison.Ordinal);
        Assert.All(titles, t => Assert.True(t.Length <= WorkspaceNamePrompt.ItemLength + 1, t));
        Assert.True(titles.Sum(t => t.Length) <= WorkspaceNamePrompt.ConversationChars);
        Assert.DoesNotContain("分支里的问题", sent, StringComparison.Ordinal);
        Assert.Contains("会话标题（共 25 个，列出 20 个）", sent, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidates_are_cleaned_as_titles_are_and_invalid_or_repeated_ones_are_dropped()
    {
        var output = string.Join('\n',
            "候选名称：",
            "1. “蒸汽机车史”",
            "2、《死寂》与恐怖片",
            "- 测试",
            "3) 铁路发展。",
            "铁路发展",
            "这一行是一段解释说明而不是名称，它远远超过了二十个字的长度限制",
            "**火车与工业**",
            "第四个");

        var names = WorkspaceNamePrompt.ParseCandidates(output, Name("测试"));

        // Pair-aware: the book-title marks that belong to the name stay; the current name and a repeat are dropped;
        // at most three are kept.
        Assert.Equal(["蒸汽机车史", "《死寂》与恐怖片", "铁路发展"], names.Select(n => n.Value));
        Assert.Equal(["火车与工业", "第四个"], WorkspaceNamePrompt.ParseCandidates("**火车与工业**\n  第四个  \n\n", Name("测试")).Select(n => n.Value));
        Assert.Empty(WorkspaceNamePrompt.ParseCandidates("测试\n“测试”\n。\n", Name("测试")));
    }

    [Fact]
    public async Task Without_a_model_or_anything_in_the_workspace_nothing_is_sent()
    {
        await using (var unconfigured = await ChatHarness.StartAsync(configure: false))
        {
            var result = await unconfigured.NameSuggester.SuggestAsync(unconfigured.Workspace, Name("测试"), Ct);
            Assert.Equal(NameSuggestionProblem.NotConfigured, result.Problem);
            Assert.Empty(unconfigured.Client.Requests);
        }

        await using var h = await ChatHarness.StartAsync();
        var empty = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        Assert.Equal(NameSuggestionProblem.EmptyWorkspace, empty.Problem);
        Assert.Empty(empty.Names);
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public async Task A_failed_cut_off_or_unusable_answer_says_why_and_stores_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        await StartAsync(h, "蒸汽机是怎么工作的？", "蒸汽机的原理");
        var before = await StoredRowsAsync(h);

        h.Client.WorkspaceNames.FailNext(GenerationErrorKind.Network);
        var failed = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        Assert.Equal((NameSuggestionProblem.Failed, GenerationErrorKind.Network), (failed.Problem!.Value, failed.Error!.Value));

        h.Client.WorkspaceNames.TruncateNext();
        var cut = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        Assert.Equal((NameSuggestionProblem.Failed, GenerationErrorKind.InvalidResponse), (cut.Problem!.Value, cut.Error!.Value));

        h.Client.WorkspaceNames.Replies.Enqueue("测试\n“测试”");
        var unusable = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        Assert.Null(unusable.Problem);
        Assert.Empty(unusable.Names);

        Assert.Equal(before, await StoredRowsAsync(h));
        Assert.Equal(0, h.Generation.UnfinishedCount);
    }

    [Fact]
    public async Task Cancelling_stops_a_running_or_queued_request_and_frees_its_slot()
    {
        await using var h = await ChatHarness.StartAsync(slots: 1);
        await StartAsync(h, "蒸汽机是怎么工作的？", "蒸汽机的原理");

        // Running: the service is held; cancelling stops the stream.
        var gate = h.Client.WorkspaceNames.Hold();
        using (var stop = new CancellationTokenSource())
        {
            var running = h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), stop.Token);
            await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }

        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        gate.TrySetResult();

        // Queued behind an answer in the only slot: cancelling takes it off the queue; it is never sent.
        var answer = h.Client.Hold("部分");
        await h.Conversations.StartAsync(h.Workspace, "铁路有多长？", Ct);
        await answer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var sentBefore = h.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming);
        using (var stop = new CancellationTokenSource())
        {
            var queued = h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), stop.Token);
            await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 2);
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        }

        answer.Finish("完");
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        Assert.Equal(sentBefore, h.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming));
    }

    [Fact]
    public async Task An_exit_stops_a_running_request_without_asking_and_refuses_new_ones_until_reopened()
    {
        await using var h = await ChatHarness.StartAsync();
        await StartAsync(h, "蒸汽机是怎么工作的？", "蒸汽机的原理");
        h.Client.WorkspaceNames.Hold();
        var running = h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));

        // Not something the exit prompt asks about (it is not an answer or a summary the user would lose).
        Assert.False(h.Generation.UnfinishedUserWork.Any);
        Assert.True(await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        var refused = await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        Assert.Equal(NameSuggestionProblem.Unavailable, refused.Problem);
        h.Generation.Reopen();
        h.Client.WorkspaceNames.Gate = null;
        Assert.Equal(3, (await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct)).Names.Count);
    }

    private static async Task<ConversationId> StartAsync(ChatHarness h, string question, string? title)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        if (title is null)
        {
            await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
            return sent.Conversation;
        }

        await h.SettleAsync(sent.Conversation);
        await h.Conversations.RenameAsync(sent.Conversation, title, Ct);
        return sent.Conversation;
    }

    private static Task<(long Tasks, long Requests)> StoredRowsAsync(ChatHarness h) =>
        h.Database.ReadAsync(
            c => (c.Scalar<long>(null, "SELECT count(*) FROM generation_task;"), c.Scalar<long>(null, "SELECT count(*) FROM ai_request;")),
            CancellationToken.None);
}
