using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Summary transactions (architecture §8.2): each operation applies whole or not at all, with its guards checked inside.</summary>
public sealed class SummaryStoreTests : IAsyncLifetime
{
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private static readonly ConversationId Conversation = ConversationId.Parse("0190f000-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    private readonly TempDataDirectory _dir = new();
    private SqliteDatabase _database = null!;
    private SqliteSummaryStore _store = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = _dir.Database();
        Assert.True((await _database.OpenAsync(Ct)).IsReady);
        _store = new SqliteSummaryStore(_database);
        await _database.WriteAsync((c, t) => c.Execute(t, $"""
            INSERT INTO workspace VALUES ('{Workspace}', '火车', 1);
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            """), Ct);
        await AddMessagesAsync(0, 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public async Task The_first_summary_creates_the_node_with_its_current_version_and_completes_the_task_together()
    {
        var task = await RunningTaskAsync();

        var saved = await _store.SaveGeneratedAsync(task, Conversation, "蒸汽机", "正文", 1, null, "m", At, Ct);

        Assert.False(saved!.IsCandidate);
        var detail = (await _store.GetAsync(saved.Node, Ct))!;
        Assert.Equal(saved.Version, detail.Node.Current);
        Assert.Equal(("蒸汽机", NameSource.Base), (detail.Node.DisplayName, detail.Node.NameSource));
        Assert.Equal(new SummaryMarkers(Unread: true, HasCandidate: false, HasLaterContent: false), detail.Markers);
        Assert.Equal("completed", await TaskStateAsync(task));
    }

    [Fact]
    public async Task A_task_stopped_meanwhile_saves_nothing()
    {
        var task = await RunningTaskAsync();
        await _database.WriteAsync((c, t) => c.Execute(t, $"UPDATE generation_task SET state = 'stopped' WHERE id = '{task}';"), Ct);

        Assert.Null(await _store.SaveGeneratedAsync(task, Conversation, "蒸汽机", "正文", 1, null, "m", At, Ct));
        Assert.Null(await _store.ForConversationAsync(Conversation, Ct));
    }

    [Fact]
    public async Task Summarizing_again_adds_a_candidate_and_later_messages_show_as_later_content()
    {
        var first = await FirstAsync();
        await AddMessagesAsync(2, 3);
        Assert.True((await _store.ForConversationAsync(Conversation, Ct))!.Markers.HasLaterContent);

        var second = await _store.SaveGeneratedAsync(await RunningTaskAsync(), Conversation, "蒸汽机", "新正文", 3, first.Version, "m", At, Ct);

        Assert.True(second!.IsCandidate);
        var detail = (await _store.GetAsync(first.Node, Ct))!;
        Assert.Equal(first.Version, detail.Node.Current);
        Assert.Equal(second.Version, detail.Pending!.Id);
        Assert.True(detail.Markers.HasCandidate);
        Assert.True(detail.Markers.HasLaterContent);
    }

    [Fact]
    public async Task Adopting_a_read_candidate_keeps_it_read_and_discarding_removes_it()
    {
        var first = await FirstAsync();
        var candidate = await CandidateAsync(first);
        await _store.SetReadAsync(candidate, ReadSource.Manual, At, Ct);

        await _store.AdoptAsync(first.Node, candidate, At, Ct);

        var detail = (await _store.GetAsync(first.Node, Ct))!;
        Assert.Equal(candidate, detail.Node.Current);
        Assert.Null(detail.Node.Pending);
        Assert.False(detail.Markers.Unread);

        var next = await CandidateAsync(first);
        await _store.DiscardAsync(first.Node, next, At, Ct);
        detail = (await _store.GetAsync(first.Node, Ct))!;
        Assert.Null(detail.Node.Pending);
        Assert.DoesNotContain(detail.Versions, v => v.Id == next);
    }

    [Fact]
    public async Task An_unsaved_edit_blocks_adopting_switching_and_deleting()
    {
        var first = await FirstAsync();
        var candidate = await CandidateAsync(first);
        await _store.SaveDraftAsync(new SummaryEditDraft(first.Node, first.Version, "蒸汽机", "改了一半", At), Ct);

        Assert.Equal(SummaryRefusal.HasDraft, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.AdoptAsync(first.Node, candidate, At, Ct))).Reason);
        Assert.Equal(SummaryRefusal.HasDraft, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.SetCurrentAsync(first.Node, first.Version, At, Ct))).Reason);
        Assert.Equal(SummaryRefusal.HasDraft, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.DeleteVersionsAsync(first.Node, new HashSet<SummaryVersionId> { first.Version }, At, Ct))).Reason);
        Assert.Equal(candidate, (await _store.GetAsync(first.Node, Ct))!.Node.Pending);
    }

    [Fact]
    public async Task Saving_an_edit_makes_a_read_current_version_with_the_same_coverage_and_an_unchanged_one_adds_nothing()
    {
        var first = await FirstAsync();
        await _store.SaveDraftAsync(new SummaryEditDraft(first.Node, first.Version, "蒸汽机", "正文", At), Ct);

        Assert.Null(await _store.SaveEditAsync(first.Node, first.Version, "蒸汽机", "正文", At, Ct));
        Assert.Null((await _store.GetAsync(first.Node, Ct))!.Draft);

        var edited = await _store.SaveEditAsync(first.Node, first.Version, "蒸汽机", "我的改写", At, Ct);

        var detail = (await _store.GetAsync(first.Node, Ct))!;
        Assert.Equal(edited, detail.Node.Current);
        Assert.Equal((SummaryOrigin.Edited, 1, first.Version), (detail.Current.Origin, detail.Current.ToSeq, detail.Current.Baseline!.Value));
        Assert.Equal(ReadSource.Edit, detail.ReadState(edited!.Value).Source);
        Assert.Equal(2, detail.Versions.Count);
    }

    [Fact]
    public async Task Deleting_the_current_version_switches_to_the_previous_kept_one_and_deleting_the_last_deletes_the_node()
    {
        var first = await FirstAsync();
        var second = await _store.SaveEditAsync(first.Node, first.Version, "蒸汽机", "第二版", At, Ct);
        var third = await _store.SaveEditAsync(first.Node, second!.Value, "蒸汽机", "第三版", At, Ct);

        var result = await _store.DeleteVersionsAsync(first.Node, new HashSet<SummaryVersionId> { third!.Value }, At, Ct);

        Assert.Equal(second, result.NewCurrent);
        Assert.Equal(second, (await _store.GetAsync(first.Node, Ct))!.Node.Current);

        var all = await _store.DeleteVersionsAsync(first.Node, new HashSet<SummaryVersionId> { first.Version, second.Value }, At, Ct);

        Assert.Null(all.NewCurrent);
        Assert.Null(await _store.GetAsync(first.Node, Ct));
        Assert.Equal(0, await CountAsync("summary_version"));
        Assert.Equal(0, await CountAsync("summary_read_state"));
    }

    [Fact]
    public async Task The_last_saved_version_cannot_go_while_a_candidate_waits_and_a_candidate_is_not_deleted_as_a_version()
    {
        var first = await FirstAsync();
        var candidate = await CandidateAsync(first);

        Assert.Equal(SummaryRefusal.HasCandidate, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.DeleteVersionsAsync(first.Node, new HashSet<SummaryVersionId> { first.Version }, At, Ct))).Reason);
        Assert.Equal(SummaryRefusal.IsCandidate, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.DeleteVersionsAsync(first.Node, new HashSet<SummaryVersionId> { candidate }, At, Ct))).Reason);
        Assert.Equal(SummaryRefusal.IsCandidate, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.SetCurrentAsync(first.Node, candidate, At, Ct))).Reason);
        Assert.Equal(2, await CountAsync("summary_version"));
    }

    [Fact]
    public async Task Versions_cannot_be_deleted_while_a_summary_task_runs()
    {
        var first = await FirstAsync();
        await RunningTaskAsync();

        Assert.Equal(SummaryRefusal.TaskRunning, (await Assert.ThrowsAsync<SummaryRefusedException>(() => _store.DeleteVersionsAsync(first.Node, new HashSet<SummaryVersionId> { first.Version }, At, Ct))).Reason);
    }

    [Fact]
    public async Task Setting_an_older_version_current_keeps_every_version_and_read_state()
    {
        var first = await FirstAsync();
        await _store.SetReadAsync(first.Version, ReadSource.Manual, At, Ct);
        var second = await _store.SaveEditAsync(first.Node, first.Version, "蒸汽机", "第二版", At, Ct);

        await _store.SetCurrentAsync(first.Node, first.Version, At, Ct);

        var detail = (await _store.GetAsync(first.Node, Ct))!;
        Assert.Equal(first.Version, detail.Node.Current);
        Assert.Equal(2, detail.Versions.Count);
        Assert.True(detail.ReadState(second!.Value).IsRead);
        Assert.False(detail.Markers.Unread);
    }

    [Fact]
    public async Task Progress_only_rises_and_never_clears_a_read_mark_while_the_toggle_can()
    {
        var first = await FirstAsync();

        await _store.RecordProgressAsync(first.Version, 0.5, "{\"a\":1}", markRead: false, At, Ct);
        await _store.RecordProgressAsync(first.Version, 0.3, null, markRead: false, At, Ct);
        var state = (await _store.GetAsync(first.Node, Ct))!.ReadState(first.Version);
        Assert.Equal((0.5, false, "{\"a\":1}"), (state.MaxProgress, state.IsRead, state.Locator));

        await _store.RecordProgressAsync(first.Version, 0.95, null, markRead: true, At, Ct);
        Assert.Equal(ReadSource.Progress, (await _store.GetAsync(first.Node, Ct))!.ReadState(first.Version).Source);

        await _store.SetReadAsync(first.Version, null, At, Ct);
        state = (await _store.GetAsync(first.Node, Ct))!.ReadState(first.Version);
        Assert.Equal((0.95, false), (state.MaxProgress, state.IsRead));
    }

    [Fact]
    public async Task Automatic_names_apply_only_while_the_node_is_unchanged_and_never_over_a_manual_name()
    {
        var first = await FirstAsync();
        var node = (await _store.GetAsync(first.Node, Ct))!.Node;
        var stale = new NamingExpectation(node.Id, node.Current, "别的名字");

        Assert.Empty(await _store.ApplyNamesAsync([(stale, "蒸汽机－原理")], At, Ct));

        var expected = new NamingExpectation(node.Id, node.Current, node.DisplayName);
        Assert.Single(await _store.ApplyNamesAsync([(expected, "蒸汽机－原理")], At, Ct));
        Assert.Equal(("蒸汽机－原理", NameSource.Auto), Name(await _store.GetAsync(first.Node, Ct)));

        await _store.RenameAsync(first.Node, "我的蒸汽机", At, Ct);
        Assert.Empty(await _store.ApplyNamesAsync([(expected with { DisplayName = "我的蒸汽机" }, "蒸汽机－局限")], At, Ct));
        await _store.UpdateBaseTitleAsync(first.Node, first.Version, "新标题", At, Ct);
        Assert.Equal(("我的蒸汽机", NameSource.Manual), Name(await _store.GetAsync(first.Node, Ct)));
    }

    private static (string, NameSource) Name(SummaryDetail? detail) => (detail!.Node.DisplayName, detail.Node.NameSource);

    private async Task<SummarySaved> FirstAsync() =>
        (await _store.SaveGeneratedAsync(await RunningTaskAsync(), Conversation, "蒸汽机", "正文", 1, null, "m", At, Ct))!;

    private async Task<SummaryVersionId> CandidateAsync(SummarySaved first) =>
        (await _store.SaveGeneratedAsync(await RunningTaskAsync(), Conversation, "蒸汽机", $"候选 {Guid.NewGuid()}", 1, first.Version, "m", At, Ct))!.Version;

    private async Task<GenerationTaskId> RunningTaskAsync()
    {
        var id = GenerationTaskId.New();
        await _database.WriteAsync((c, t) => c.Execute(t, $"""
            INSERT INTO generation_task (id, kind, conversation_id, state, model_config_id, base_url, protocol, model, credential_ref,
                                         input_message_ids, created_at, summary_to_seq)
                VALUES ('{id}', 'summary', '{Conversation}', 'running', 'm', 'https://fake.example/v1', 'chat_completions', 'fake', 'c/k', '[]', 2, 1);
            """), Ct);
        return id;
    }

    private Task<int> AddMessagesAsync(int question, int answer) =>
        _database.WriteAsync((c, t) => c.Execute(t, $"""
            INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at) VALUES ('{MessageId.New()}', '{Conversation}', {question}, 'user', 'complete', 1, 1);
            INSERT INTO message (id, conversation_id, seq, role, state, created_at, completed_at) VALUES ('{MessageId.New()}', '{Conversation}', {answer}, 'assistant', 'complete', 1, 1);
            """), Ct);

    private Task<string?> TaskStateAsync(GenerationTaskId task) =>
        _database.ReadAsync(c => c.Scalar<string>(null, $"SELECT state FROM generation_task WHERE id = '{task}';"), Ct);

    private Task<long> CountAsync(string table) => _database.ReadAsync(c => c.Scalar<long>(null, $"SELECT count(*) FROM {table};"), Ct);
}
