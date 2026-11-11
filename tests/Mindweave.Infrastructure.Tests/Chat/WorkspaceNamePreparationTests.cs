using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// A workspace's names prepared in the background and kept (user decision of 2026-11-11): when they are due (T(n)), the
/// 30 s wait after the last new title (on a standing clock), never ahead of or in the way of other work, never while off,
/// paused, unconfigured or exiting; kept across a restart, following a rename and a delete; every request recorded for
/// its workspace with its token use.
/// </summary>
public class WorkspaceNamePreparationTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 3)]
    [InlineData(20, 3)]
    [InlineData(21, 4)]
    [InlineData(26, 4)]
    [InlineData(27, 5)]
    [InlineData(40, 6)]
    [InlineData(100, 15)]
    [InlineData(133, 20)]
    [InlineData(134, 20)]
    [InlineData(1000, 20)]
    public void The_threshold_grows_with_the_workspace_and_is_capped(int titled, int threshold) =>
        Assert.Equal(threshold, NameRefreshRule.Threshold(titled));

    [Fact]
    public void Names_are_due_at_three_titles_then_after_growing_by_the_threshold()
    {
        static PreparedNames Kept(int count) => new([], DateTimeOffset.UnixEpoch, count);

        Assert.False(NameRefreshRule.IsDue(2, null));
        Assert.True(NameRefreshRule.IsDue(3, null));
        Assert.False(NameRefreshRule.IsDue(5, Kept(3)));
        Assert.True(NameRefreshRule.IsDue(6, Kept(3)));
        Assert.False(NameRefreshRule.IsDue(35, Kept(30)));  // T(35) = 6
        Assert.True(NameRefreshRule.IsDue(36, Kept(30)));
        Assert.False(NameRefreshRule.IsDue(160, Kept(141))); // capped at 20
        Assert.True(NameRefreshRule.IsDue(161, Kept(141)));

        // Deleted or archived conversations lower the count; growth counts from the kept one.
        Assert.False(NameRefreshRule.IsDue(9, Kept(10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NameRefreshRule.Threshold(-1));
    }

    [Fact]
    public void Kept_names_are_read_back_as_written_and_anything_unreadable_is_none()
    {
        var names = new PreparedNames([Name("蒸汽机车史"), Name("铁路发展")], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), 7);
        var back = WorkspaceNameCache.Parse(WorkspaceNameCache.Format(names))!;
        Assert.Equal(["蒸汽机车史", "铁路发展"], back.Names.Select(n => n.Value));
        Assert.Equal((names.At, 7), (back.At, back.TitledConversations));
        Assert.Null(WorkspaceNameCache.Parse(null));
        Assert.Null(WorkspaceNameCache.Parse("not json"));
        Assert.Null(WorkspaceNameCache.Parse("{\"names\":[]}"));
        Assert.Equal(["好的"], WorkspaceNameCache.Parse("{\"names\":[\"  \",\"好的\"],\"at\":1,\"count\":3}")!.Names.Select(n => n.Value));
    }

    [Fact]
    public async Task The_wait_restarts_with_each_new_title_and_one_request_follows_the_last()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        await TitledAsync(h, "蒸汽机是怎么工作的？");
        await TitledAsync(h, "铁轨为什么是工字形的？");
        Assert.False(preparer.IsWaiting(h.Workspace));

        // The third title makes them due: the wait starts.
        await TitledAsync(h, "第一条铁路在哪里？");
        Assert.True(preparer.IsWaiting(h.Workspace));
        await AdvanceAsync(h, TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);

        // A fourth title 20 s in starts the 30 s again.
        await TitledAsync(h, "蒸汽机车跑多快？");
        await AdvanceAsync(h, TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        await AdvanceAsync(h, TimeSpan.FromSeconds(10));

        Assert.Single(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Equal(1, preparer.Sent);
        Assert.False(preparer.IsWaiting(h.Workspace));
        var kept = (await h.NameCache.GetAsync(h.Workspace, null, Ct))!;
        Assert.Equal(["蒸汽机车史", "铁路发展", "火车与工业革命"], kept.Names.Select(n => n.Value));
        Assert.Equal(4, kept.TitledConversations);

        // A fifth title: grown by one of the three needed, so nothing more is due.
        await TitledAsync(h, "火车为什么要鸣笛？");
        Assert.False(preparer.IsWaiting(h.Workspace));
        await AdvanceAsync(h, Wait);
        Assert.Single(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
    }

    [Fact]
    public async Task A_conversation_renamed_again_does_not_restart_a_running_wait_but_a_manual_title_counts()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        h.Client.Titles.FailNext(GenerationErrorKind.ServerError);
        var untitled = await StartAsync(h, "没有标题的会话");
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        var first = await TitledAsync(h, "蒸汽机是怎么工作的？");
        await TitledAsync(h, "铁轨为什么是工字形的？");
        Assert.False(preparer.IsWaiting(h.Workspace));

        // Titled by hand: the third title.
        await h.Conversations.RenameAsync(untitled, "手写的标题", Ct);
        await preparer.SettledAsync();
        Assert.True(preparer.IsWaiting(h.Workspace));
        await AdvanceAsync(h, TimeSpan.FromSeconds(25));

        // Renaming a titled one again is no new title: the wait keeps its end.
        await h.Conversations.RenameAsync(first, "改过的标题", Ct);
        await preparer.SettledAsync();
        await AdvanceAsync(h, TimeSpan.FromSeconds(5));
        Assert.Single(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
    }

    [Fact]
    public async Task A_failed_background_request_shows_nothing_and_the_next_new_title_tries_again()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        await ThreeTitlesAsync(h);
        h.Client.WorkspaceNames.FailNext(GenerationErrorKind.ServerError);
        await AdvanceAsync(h, Wait);
        Assert.Equal(1, preparer.Sent);
        Assert.Null(await h.NameCache.GetAsync(h.Workspace, null, Ct));
        Assert.False(preparer.IsWaiting(h.Workspace));

        // Recorded like any request; tried again once another conversation has its title.
        Assert.Equal(Application.Diagnostics.AiRequestOutcome.Failed, Assert.Single(await h.Requests.ForWorkspaceAsync(h.Workspace, Ct)).Outcome);
        await TitledAsync(h, "第四个问题");
        Assert.True(preparer.IsWaiting(h.Workspace));
        await AdvanceAsync(h, Wait);
        Assert.Equal(2, preparer.Sent);
        Assert.Equal(4, (await h.NameCache.GetAsync(h.Workspace, null, Ct))!.TitledConversations);
    }

    [Fact]
    public async Task Nothing_is_sent_while_turned_off_and_turning_it_off_during_the_wait_stops_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        await h.Hints.SetPrepareNamesAsync(false, Ct);
        await ThreeTitlesAsync(h);
        Assert.False(preparer.IsWaiting(h.Workspace));
        await AdvanceAsync(h, Wait * 2);

        await h.Hints.SetPrepareNamesAsync(true, Ct);
        await TitledAsync(h, "第四个问题");
        Assert.True(preparer.IsWaiting(h.Workspace));
        await h.Hints.SetPrepareNamesAsync(false, Ct);
        await AdvanceAsync(h, Wait);

        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Equal(0, preparer.Sent);
        Assert.Null(await h.NameCache.GetAsync(h.Workspace, null, Ct));
    }

    [Fact]
    public async Task Nothing_is_sent_while_storage_is_paused()
    {
        await using var h = await ChatHarness.StartAsync(storageRetry: TimeSpan.FromHours(1));
        var preparer = h.NamePreparer;
        await ThreeTitlesAsync(h);
        Assert.True(preparer.IsWaiting(h.Workspace));
        h.Health.Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await AdvanceAsync(h, Wait);

        Assert.Equal(0, preparer.Sent);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Null(await h.NameCache.GetAsync(h.Workspace, null, Ct));
    }

    [Fact]
    public async Task Without_a_model_the_wait_ends_quietly_and_nothing_is_sent()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        await ThreeTitlesAsync(h);
        Assert.True(preparer.IsWaiting(h.Workspace));
        await h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM app_state WHERE key = 'active_model_config_id';"), Ct);
        Assert.Null(await h.Models.GetActiveSnapshotAsync(Ct));
        await AdvanceAsync(h, Wait);

        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Null(await h.NameCache.GetAsync(h.Workspace, null, Ct));
        Assert.Empty(await h.Requests.ForWorkspaceAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task An_exit_stops_a_running_request_and_none_is_sent_while_it_lasts()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        await ThreeTitlesAsync(h);
        h.Client.WorkspaceNames.Hold();
        h.PreparationTime.Advance(Wait);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));

        // Not something the exit prompt asks about; stopped silently, nothing kept.
        Assert.False(h.Generation.UnfinishedUserWork.Any);
        Assert.True(await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5)));
        await preparer.SettledAsync();
        Assert.Null(await h.NameCache.GetAsync(h.Workspace, null, Ct));

        // During the exit a wait that ends sends nothing.
        h.Generation.Reopen();
        await TitledAsync(h, "第四个问题");
        await TitledAsync(h, "第五个问题");
        Assert.True(preparer.IsWaiting(h.Workspace));
        Assert.True(await h.Generation.StopForExitAsync(TimeSpan.FromSeconds(5)));
        await AdvanceAsync(h, Wait);
        Assert.Single(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);
        Assert.Equal(1, preparer.Sent);
    }

    [Fact]
    public async Task A_prepared_request_waits_behind_the_users_work_and_gives_its_slot_up_to_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        var roots = await ThreeTitlesAsync(h);

        // Both slots answer; the names wait in the queue, and an answer asked for after them still goes first.
        var first = h.Client.Hold("一");
        var second = h.Client.Hold("二");
        var third = h.Client.Hold("三");
        await h.Conversations.AskAsync(roots[0], "再问一", Ct);
        await h.Conversations.AskAsync(roots[1], "再问二", Ct);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        h.PreparationTime.Advance(Wait);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 3);
        await h.Conversations.AskAsync(roots[2], "再问三", Ct);
        first.Finish("完");
        await third.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.DoesNotContain(h.Client.Requests, FakeChatClient.IsWorkspaceNaming);

        // Once a slot is free of the user's work, the names go.
        second.Finish("完");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));
        third.Finish("完");
        await preparer.SettledAsync();
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        Assert.NotNull(await h.NameCache.GetAsync(h.Workspace, null, Ct));
    }

    [Fact]
    public async Task A_running_prepared_request_gives_its_slot_to_an_answer_and_is_asked_again_later()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        var roots = await ThreeTitlesAsync(h);
        var gate = h.Client.WorkspaceNames.Hold();
        var first = h.Client.Hold("一");
        await h.Conversations.AskAsync(roots[0], "再问一", Ct);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        h.PreparationTime.Advance(Wait);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));

        // Every slot is taken (an answer and the names): a new answer does not wait for the names.
        var second = h.Client.Hold("二");
        await h.Conversations.AskAsync(roots[1], "再问二", Ct);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await preparer.SettledAsync();
        Assert.Null(await h.NameCache.GetAsync(h.Workspace, null, Ct));
        Assert.True(preparer.IsWaiting(h.Workspace));

        // Asked again after the usual wait, once there is room.
        h.Client.WorkspaceNames.Gate = null;
        gate.TrySetResult();
        first.Finish("完");
        second.Finish("完");
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        await AdvanceAsync(h, Wait);
        Assert.Equal(2, h.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming));
        Assert.NotNull(await h.NameCache.GetAsync(h.Workspace, null, Ct));
    }

    [Fact]
    public async Task One_prepared_request_at_a_time_in_the_whole_application()
    {
        await using var h = await ChatHarness.StartAsync();
        var preparer = h.NamePreparer;
        var other = await h.WorkspaceService.CreateAsync(Name("城市交通"), Ct);
        await ThreeTitlesAsync(h);
        foreach (var question in new[] { "第一条地铁在哪里？", "公交车是谁发明的？", "有轨电车还有吗？" })
        {
            await TitledAsync(h, question, other.Id);
        }

        var gate = h.Client.WorkspaceNames.Hold();
        h.PreparationTime.Advance(Wait);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsWorkspaceNaming));
        await ChatHarness.StaysAsync(() => h.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming) == 1, 200);

        h.Client.WorkspaceNames.Gate = null;
        gate.TrySetResult();
        await ChatHarness.UntilAsync(() => h.Client.Requests.Count(FakeChatClient.IsWorkspaceNaming) == 2);
        await preparer.SettledAsync();
        Assert.NotNull(await h.NameCache.GetAsync(h.Workspace, null, Ct));
        Assert.NotNull(await h.NameCache.GetAsync(other.Id, null, Ct));
    }

    [Fact]
    public async Task Requests_are_recorded_for_their_workspace_with_the_tokens_the_service_reports()
    {
        await using var h = await ChatHarness.StartAsync();
        await TitledAsync(h, "蒸汽机是怎么工作的？");
        var tasksBefore = await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task;"), Ct);
        h.Client.PromptTokens = r => FakeChatClient.IsWorkspaceNaming(r) ? 321 : null;
        h.Client.CompletionTokens = r => FakeChatClient.IsWorkspaceNaming(r) ? 45 : null;

        await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);
        h.Client.WorkspaceNames.FailNext(GenerationErrorKind.Network);
        await h.NameSuggester.SuggestAsync(h.Workspace, Name("测试"), Ct);

        var records = await h.Requests.ForWorkspaceAsync(h.Workspace, Ct);
        Assert.Equal(2, records.Count);
        Assert.All(records, r =>
        {
            Assert.Null(r.TaskId);
            Assert.Equal(new RequestAttribution(h.Workspace, "workspace-name"), r.Attribution);
            Assert.Equal(("fake-model", "https://fake.example/v1"), (r.Model, r.BaseUrl));
            Assert.NotNull(r.EstimatedTokens);
        });
        Assert.Equal((Application.Diagnostics.AiRequestOutcome.Completed, 321, 45), (records[0].Outcome, records[0].PromptTokens, records[0].CompletionTokens));
        Assert.Equal((Application.Diagnostics.AiRequestOutcome.Failed, GenerationErrorKind.Network), (records[1].Outcome, records[1].ErrorKind));

        // No task row stands for them.
        Assert.Equal(tasksBefore, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task;"), Ct));
    }

    [Fact]
    public async Task Kept_names_survive_a_restart_and_follow_a_rename_and_a_delete()
    {
        using var dir = new TempDataDirectory();
        WorkspaceId workspace;
        await using (var h = await ChatHarness.StartAsync(directory: dir))
        {
            workspace = h.Workspace;
            await TitledAsync(h, "蒸汽机是怎么工作的？");
            await TitledAsync(h, "铁轨为什么是工字形的？");
            Assert.Equal(3, (await h.NameSuggester.SuggestAsync(workspace, Name("测试"), Ct)).Names.Count);
        }

        await using var again = await ChatHarness.StartAsync(directory: dir);
        var kept = (await again.NameCache.GetAsync(workspace, Name("测试"), Ct))!;
        Assert.Equal(["蒸汽机车史", "铁路发展", "火车与工业革命"], kept.Names.Select(n => n.Value));
        Assert.Equal(2, kept.TitledConversations);

        // Renamed to one of them: that one is no longer offered, the others stay.
        await again.WorkspaceService.RenameAsync(workspace, Name("铁路发展"), Ct);
        Assert.Equal(["蒸汽机车史", "火车与工业革命"], (await again.NameCache.GetAsync(workspace, null, Ct))!.Names.Select(n => n.Value));
        Assert.Equal(["蒸汽机车史", "火车与工业革命"], (await again.NameCache.GetAsync(workspace, Name("铁路发展"), Ct))!.Names.Select(n => n.Value));

        // Deleted: its names and its recorded requests go with it.
        Assert.Single(await again.Requests.ForWorkspaceAsync(workspace, Ct));
        Assert.Equal(WorkspaceDeleteStatus.Deleted, (await again.WorkspaceDeletion.DeleteAsync(workspace, Ct)).Status);
        Assert.Null(await again.NameCache.GetAsync(workspace, null, Ct));
        Assert.Equal(0, await again.Database.ReadAsync(c => c.Scalar<long>(null, $"SELECT count(*) FROM app_state WHERE key LIKE '%{workspace}%';"), Ct));
        Assert.Empty(await again.Requests.ForWorkspaceAsync(workspace, Ct));
    }

    private static WorkspaceName Name(string raw)
    {
        Assert.True(WorkspaceName.TryCreate(raw, out var name, out _));
        return name!;
    }

    /// <summary>Moves the standing clock, then waits for whatever that started (a request and its keeping) to end.</summary>
    private static async Task AdvanceAsync(ChatHarness h, TimeSpan by)
    {
        h.PreparationTime.Advance(by);
        await h.NamePreparer.SettledAsync();
    }

    private static async Task<ConversationId> StartAsync(ChatHarness h, string question, WorkspaceId? workspace = null)
    {
        var sent = await h.Conversations.StartAsync(workspace ?? h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        return sent.Conversation;
    }

    /// <summary>A new conversation with its automatic title stored, and the background done looking at it.</summary>
    private static async Task<ConversationId> TitledAsync(ChatHarness h, string question, WorkspaceId? workspace = null)
    {
        var conversation = await StartAsync(h, question, workspace);
        await h.SettleAsync(conversation);
        await h.NamePreparer.SettledAsync();
        return conversation;
    }

    private static async Task<IReadOnlyList<ConversationId>> ThreeTitlesAsync(ChatHarness h) =>
    [
        await TitledAsync(h, "蒸汽机是怎么工作的？"),
        await TitledAsync(h, "铁轨为什么是工字形的？"),
        await TitledAsync(h, "第一条铁路在哪里？"),
    ];
}
