using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Application.Summaries;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Archive, restore and delete (requirements §9.5, D-021) through the real services and SQLite: hidden is derived, never
/// written to descendants; deep restore clears the path only; deletes check and remove in one transaction.
/// </summary>
public class ArchiveFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_separately_archived_branch_stays_archived_when_its_parent_is_archived_and_restored()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var child = await h.BranchAsync(root, answer, "甲");

        Assert.True((await h.Conversations.ArchiveAsync(child, Ct)).Archived);
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
        var listed = await h.Conversations.ListAsync(h.Workspace, Ct);
        Assert.All(listed, c => Assert.True(c.IsHidden));
        Assert.Equal(2, await h.CountAsync("conversation_archive"));

        var shown = await h.Conversations.RestoreAsync(root, Ct);

        Assert.Equal([root], shown);
        listed = await h.Conversations.ListAsync(h.Workspace, Ct);
        Assert.False(listed.Single(c => c.Conversation.Id == root).IsHidden);
        var stillArchived = listed.Single(c => c.Conversation.Id == child);
        Assert.True(stillArchived.IsArchived && stillArchived.IsHidden);
    }

    [Fact]
    public async Task Archiving_writes_one_row_and_hides_the_whole_subtree_by_derivation()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var child = await h.BranchAsync(root, answer, "甲");
        var childAnswer = await h.AskAsync(child, "子问");
        var grandchild = await h.BranchAsync(child, childAnswer, "乙");
        var (other, _) = await h.RootWithAnswerAsync("别的");

        var outcome = await h.Conversations.ArchiveAsync(child, Ct);

        Assert.True(outcome.Archived);
        Assert.Equal(1, await h.CountAsync("conversation_archive"));
        var listed = (await h.Conversations.ListAsync(h.Workspace, Ct)).ToDictionary(c => c.Conversation.Id);
        Assert.False(listed[root].IsHidden);
        Assert.False(listed[other].IsHidden);
        Assert.True(listed[child].IsArchived);
        Assert.True(listed[grandchild] is { IsHidden: true, IsArchived: false });
        Assert.Equal([child], await h.Conversations.ArchivedAmongAsync([child, grandchild, root], Ct));
    }

    [Fact]
    public async Task Restoring_a_deeply_hidden_branch_restores_its_path_only()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, rootAnswer) = await h.RootWithAnswerAsync("根");
        var a = await h.BranchAsync(root, rootAnswer, "甲");
        var aAnswer = await h.AskAsync(a, "甲问");
        var b = await h.BranchAsync(a, aAnswer, "乙");
        var a2 = await h.BranchAsync(a, aAnswer, "甲二");
        var t = await h.BranchAsync(root, rootAnswer, "丙");
        var s = await h.BranchAsync(root, rootAnswer, "丁");
        foreach (var archived in new[] { a, s, root })
        {
            Assert.True((await h.Conversations.ArchiveAsync(archived, Ct)).Archived);
        }

        var plan = await h.Conversations.PlanRestoreAsync(b, Ct);

        Assert.True(plan.IsHidden);
        Assert.False(plan.IsArchived);
        Assert.Equal([root, a], plan.ArchivedAncestors);
        // a2 and t come back with the path; s was archived separately and stays.
        Assert.Equal(2, plan.OthersReappearing);

        var shown = await h.Conversations.RestoreAsync(b, Ct);
        Assert.Equal(new HashSet<ConversationId> { root, a, b, a2, t }, shown.ToHashSet());
        var listed = (await h.Conversations.ListAsync(h.Workspace, Ct)).ToDictionary(c => c.Conversation.Id);
        Assert.All(new[] { root, a, b, a2, t }, id => Assert.False(listed[id].IsHidden));
        Assert.True(listed[s] is { IsArchived: true, IsHidden: true });
        Assert.Equal(1, await h.CountAsync("conversation_archive"));
    }

    [Fact]
    public async Task Deleting_is_refused_while_an_answer_or_summary_in_the_scope_runs_and_changes_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        await h.BranchAsync(root, answer, "甲");
        Assert.True((await h.SummaryService.SummarizeAsync(root, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(root, Ct) is { State: GenerationTaskState.Completed });
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(root, "再问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // A summary task row queued behind the UI's back (the service refuses one while an answer runs).
        await h.Store.AddTaskAsync(
            new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Summary, root, null, (await h.Models.GetActiveSnapshotAsync(Ct))!, [], DateTimeOffset.UtcNow, 1),
            Ct);
        var before = await TotalRowsAsync(h);

        var result = await h.DeleteAsync(root);

        Assert.Equal(ConversationDeleteStatus.Busy, result.Status);
        Assert.Equal([new BusyConversation(root, Answer: true, Summary: true)], result.Scope.Busy);
        Assert.Equal(before, await TotalRowsAsync(h));
        Assert.Empty(h.Database.ListBackups());
        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
    }

    [Fact]
    public async Task Deleting_a_branch_with_retries_and_regenerations_leaves_no_row_behind()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, rootAnswer) = await h.RootWithAnswerAsync("根");
        var child = await h.BranchAsync(root, rootAnswer, "甲");
        h.Client.Fail(GenerationErrorKind.ServerError);
        var failed = await h.AskAsync(child, "子问");
        var retried = await h.RetryAsync(child, failed.Id);
        var regenerated = await h.RetryAsync(child, retried.Id);
        Assert.Equal(retried.Id, regenerated.Supersedes);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        h.Reading.SetDraft(child, ViewerSlotKey.Primary, "没发出去的草稿");
        h.Reading.SetPosition(child, ViewerSlotKey.Primary, new ReadingPosition(regenerated.Id, 0, null, 1));
        await h.Reading.FlushAsync();
        await new SqliteLocationStore(h.Database, new SystemClock()).SetLastAsync(new PageLocation(h.Workspace, "explore", PageLocation.ConversationKey(child)), Ct);
        Assert.True((await h.Conversations.ArchiveAsync(child, Ct)).Archived);

        var ids = await IdsOfAsync(h, child);
        Assert.True(ids.Count > 8, "the conversation, its messages and tasks");
        var rootMessages = (await h.Store.GetMessagesAsync(root, Ct)).Count;

        var result = await h.DeleteAsync(child);

        Assert.True(result.IsDeleted);
        Assert.Equal(root, result.Scope.ParentOf[child]);
        Assert.False(result.Scope.IsCascade);
        Assert.Null(result.BackupPath);
        Assert.Empty(h.Database.ListBackups());
        Assert.Empty(await ReferencesAsync(h, ids));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
        Assert.Null((await new SqliteLocationStore(h.Database, new SystemClock()).GetLastAsync(Ct))!.ContentKey);
        Assert.Equal(rootMessages, (await h.Store.GetMessagesAsync(root, Ct)).Count);
        Assert.Empty(await h.Conversations.ListBranchesAsync(root, Ct));

        // The parent has no dependants any more and can go as well.
        Assert.True((await h.DeleteAsync(root)).IsDeleted);
        Assert.Empty(await h.Conversations.ListAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task Deleting_cancels_a_running_title_first()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var (root, _) = await h.RootWithAnswerAsync("问");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));

        var result = await h.DeleteAsync(root);

        Assert.True(result.IsDeleted);
        Assert.Equal(0, h.Generation.UnfinishedCount);
        Assert.False(h.Health.IsBlocked);
        Assert.Equal(0, await h.CountAsync("generation_task"));
        gate.TrySetResult();
    }

    [Fact]
    public async Task Archiving_cancels_a_running_title_in_the_subtree_and_it_is_asked_for_again_after_a_restore()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var (root, _) = await h.RootWithAnswerAsync("问");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Count(FakeChatClient.IsTitle) == 1);

        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);

        Assert.Equal(0, h.Generation.UnfinishedCount);
        Assert.Equal("stopped", await h.Database.ReadAsync(c => c.Scalar<string>(null, "SELECT state FROM generation_task WHERE kind = 'title';"), Ct));
        Assert.Equal(TitleSource.None, (await h.Store.GetAsync(root, Ct))!.TitleSource);
        gate.TrySetResult();

        await h.Conversations.RestoreAsync(root, Ct);
        await h.AskAsync(root, "再问");
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(root, Ct))!.TitleSource == TitleSource.Auto);
    }

    [Fact]
    public async Task A_refused_delete_leaves_the_running_title_alone()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));
        var child = await h.BranchAsync(root, answer, "甲");
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(child, "子问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var result = await h.DeleteAsync(root);

        Assert.Equal(ConversationDeleteStatus.Busy, result.Status);
        Assert.Equal([new BusyConversation(child, Answer: true, Summary: false)], result.Scope.Busy);
        Assert.Equal("running", await TitleStateAsync(h, root));
        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
        gate.TrySetResult();
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(root, Ct))!.TitleSource == TitleSource.Auto);
    }

    [Fact]
    public async Task A_refused_archive_leaves_the_titles_in_the_subtree_alone()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));
        var child = await h.BranchAsync(root, answer, "甲");
        await h.AskAsync(child, "子问");
        h.Client.KnowledgeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True((await h.SummaryService.SummarizeAsync(child, Ct)).Accepted);

        var refused = await h.Conversations.ArchiveAsync(root, Ct);

        Assert.Equal([child], refused.Busy);
        Assert.Equal(0, await h.Database.ReadAsync(
            c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task WHERE kind = 'title' AND state NOT IN ('queued', 'running');"), Ct));
        gate.TrySetResult();
        h.Client.KnowledgeGate.TrySetResult();
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(root, Ct))!.TitleSource == TitleSource.Auto
            && (await h.Store.GetAsync(child, Ct))!.TitleSource == TitleSource.Auto);
    }

    [Fact]
    public async Task A_title_stored_before_the_archive_but_queued_after_it_never_runs()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var (root, _) = await h.RootWithAnswerAsync("问");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));

        // The service's order, split by the archive: the row is stored while the conversation is visible, the work
        // reaches the queue only once the archive has committed and the coordinator's own scan is over.
        var model = (await h.Models.GetActiveSnapshotAsync(Ct))!;
        var late = new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Title, root, null, model, [], DateTimeOffset.UtcNow);
        await h.Store.AddTaskAsync(late, Ct);
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
        h.Generation.Enqueue(new TitleWork(late, new ChatRequest(model, [new ChatTurn(ChatRole.System, "起一个简短标题"), new ChatTurn(ChatRole.User, "问")])));
        gate.TrySetResult();
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);

        Assert.Equal("cancelled", await h.Database.ReadAsync(c => c.Scalar<string>(null, "SELECT state FROM generation_task WHERE id = $id;", ("$id", late.Id.ToString())), Ct));
        Assert.Equal(TitleSource.None, (await h.Store.GetAsync(root, Ct))!.TitleSource);
        Assert.False(await h.Store.SetAutoTitleAsync(root, "漏进来的标题", DateTimeOffset.UtcNow, Ct));
    }

    [Fact]
    public async Task A_late_title_for_a_conversation_deleted_under_it_is_ignored_and_storage_stays_healthy()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var (root, _) = await h.RootWithAnswerAsync("问");
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(FakeChatClient.IsTitle));

        // Straight to the store: the title task is still running when its rows disappear. Its late writes are updates
        // that find no row and a request trace whose foreign key fails (recorded best effort), so no special handling
        // is needed for storage to stay healthy.
        Assert.Equal(ConversationDeleteStatus.Deleted, (await h.Archive.DeleteAsync(await h.Archive.InspectDeleteAsync([root], Ct), Ct)).Status);
        gate.TrySetResult();
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);

        Assert.False(h.Health.IsBlocked);
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
        Assert.Equal(0, await h.CountAsync("conversation"));
        Assert.Equal(SendRejection.ConversationMissing, (await h.Conversations.AskAsync(root, "还在吗", Ct)).Rejection);
        var model = (await h.Models.GetActiveSnapshotAsync(Ct))!;
        await Assert.ThrowsAsync<ConversationMissingException>(() => h.Store.AddTaskAsync(
            new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Title, root, null, model, [], DateTimeOffset.UtcNow),
            Ct));
        Assert.True((await h.Conversations.StartAsync(h.Workspace, "新的", Ct)).Accepted);
    }

    [Fact]
    public async Task Archiving_is_refused_while_a_summary_in_the_subtree_runs_and_names_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("问");
        var child = await h.BranchAsync(root, answer, "甲");
        await h.AskAsync(child, "子问");
        h.Client.KnowledgeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True((await h.SummaryService.SummarizeAsync(child, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(child, Ct) is { State: GenerationTaskState.Running });

        var refused = await h.Conversations.ArchiveAsync(root, Ct);

        Assert.False(refused.Archived);
        Assert.Equal([child], refused.Busy);
        Assert.Equal(0, await h.CountAsync("conversation_archive"));

        h.Client.KnowledgeGate.TrySetResult();
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(child, Ct) is { State: GenerationTaskState.Completed });
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
    }

    [Fact]
    public async Task The_summary_of_an_archived_source_stays_readable_editable_and_marked()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, _) = await h.RootWithAnswerAsync("问");
        Assert.True((await h.SummaryService.SummarizeAsync(root, Ct)).Accepted);
        await ChatHarness.UntilAsync(async () => await h.SummaryService.ForConversationAsync(root, Ct) is not null);
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);

        var entry = Assert.Single(await h.SummaryService.ListAsync(h.Workspace, Ct));
        Assert.True(entry.SourceArchived);
        Assert.True(Assert.Single((await h.Network.ProjectAsync(h.Workspace, Ct)).Nodes).SourceArchived);
        Assert.NotNull(await h.Conversations.LoadAsync(root, Ct));

        // Existing summaries stay usable: read, edit, delete versions.
        await h.SummaryService.SetReadAsync(entry.Node.Id, entry.Current.Id, true, Ct);
        Assert.NotNull(await h.SummaryService.SaveEditAsync(entry.Node.Id, entry.Current.Id, "改过的标题", "改过的正文", Ct));
        Assert.NotNull(await h.SummaryService.DeleteVersionsAsync(entry.Node.Id, new HashSet<Core.Summaries.SummaryVersionId> { entry.Current.Id }, Ct));

        Assert.Equal(SummarizeRejection.ConversationArchived, (await h.SummaryService.SummarizeAsync(root, Ct)).Rejection);
        await h.Conversations.RestoreAsync(root, Ct);
        Assert.False(Assert.Single(await h.SummaryService.ListAsync(h.Workspace, Ct)).SourceArchived);
    }

    [Fact]
    public async Task New_work_on_a_hidden_conversation_is_refused_inside_the_transaction()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, rootAnswer) = await h.RootWithAnswerAsync("问");
        var child = await h.BranchAsync(root, rootAnswer, "甲");
        var childAnswer = await h.AskAsync(child, "子问");
        await ChatHarness.UntilAsync(async () => (await h.Store.GetAsync(child, Ct))!.TitleSource == TitleSource.Auto);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
        var tasks = await h.CountAsync("generation_task");

        Assert.Equal(SendRejection.ConversationArchived, (await h.Conversations.AskAsync(child, "再问", Ct)).Rejection);
        Assert.Equal(SendRejection.ConversationArchived, (await h.Conversations.RetryAsync(root, rootAnswer.Id, Ct)).Rejection);
        Assert.Equal(BranchRejection.ConversationArchived, (await h.Conversations.CreateBranchAsync(child, BranchFlowTests.Selection(childAnswer, "子"), Ct)).Rejection);
        Assert.Equal(SummarizeRejection.ConversationArchived, (await h.SummaryService.SummarizeAsync(child, Ct)).Rejection);

        // Below the services too: the stores refuse.
        var model = (await h.Models.GetActiveSnapshotAsync(Ct))!;
        var question = new Message(MessageId.New(), child, 0, MessageRole.User, ContentBlocks.FromText("绕过"), MessageState.Complete, 0, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var pending = new Message(MessageId.New(), child, 0, MessageRole.Assistant, ContentBlocks.FromText(string.Empty), MessageState.Pending, 0, null, null, null, DateTimeOffset.UtcNow, null);
        await Assert.ThrowsAsync<ConversationArchivedException>(() => h.Store.AppendQuestionAsync(
            question, pending, new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Answer, child, pending.Id, model, [], DateTimeOffset.UtcNow), Ct));
        await Assert.ThrowsAsync<ConversationArchivedException>(() => h.Store.AddTaskAsync(
            new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Summary, child, null, model, [], DateTimeOffset.UtcNow, 1), Ct));
        await Assert.ThrowsAsync<ConversationArchivedException>(() => h.Store.AddTaskAsync(
            new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Title, child, null, model, [], DateTimeOffset.UtcNow), Ct));

        Assert.Equal(tasks, await h.CountAsync("generation_task"));
        Assert.Equal(0, h.Generation.UnfinishedCount);

        // Restoring the hidden branch restores its archived ancestor, and work is accepted again.
        await h.Conversations.RestoreAsync(child, Ct);
        await h.AskAsync(child, "恢复后再问");
    }

    [Fact]
    public async Task The_hidden_check_finds_ancestors_by_the_range_primary_key()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);
        var plan = await h.Database.ReadAsync(
            c =>
            {
                using var command = c.Command(null, $"EXPLAIN QUERY PLAN SELECT {SqliteArchive.HiddenCondition("$id")};", ("$id", ConversationId.New().ToString()));
                using var reader = command.ExecuteReader();
                var lines = new List<string>();
                while (reader.Read())
                {
                    lines.Add(reader.GetString(3));
                }

                return lines;
            },
            Ct);

        Assert.Contains(plan, l => l.Contains("SEARCH hr USING PRIMARY KEY (child_id=?)", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, l => l.StartsWith("SCAN", StringComparison.Ordinal) && l != "SCAN CONSTANT ROW");
    }

    private static Task<string?> TitleStateAsync(ChatHarness h, ConversationId conversation) =>
        h.Database.ReadAsync(c => c.Scalar<string>(null, "SELECT state FROM generation_task WHERE kind = 'title' AND conversation_id = $id;", ("$id", conversation.ToString())), CancellationToken.None);

    /// <summary>The conversation's own identities: itself, its messages and its tasks.</summary>
    private static Task<HashSet<string>> IdsOfAsync(ChatHarness h, ConversationId conversation) =>
        h.Database.ReadAsync(
            c =>
            {
                var ids = new HashSet<string>(StringComparer.Ordinal) { conversation.ToString() };
                foreach (var sql in new[] { "SELECT id FROM message WHERE conversation_id = $id;", "SELECT id FROM generation_task WHERE conversation_id = $id;" })
                {
                    using var command = c.Command(null, sql, ("$id", conversation.ToString()));
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        ids.Add(reader.GetString(0));
                    }
                }

                return ids;
            },
            CancellationToken.None);

    /// <summary>Every "table.column" in the whole schema that still holds one of <paramref name="ids"/> (or the page key).</summary>
    private static Task<List<string>> ReferencesAsync(ChatHarness h, HashSet<string> ids) =>
        h.Database.ReadAsync(
            c =>
            {
                var values = ids.Concat(ids.Select(id => "conversation:" + id)).ToList();
                var tables = new List<string>();
                using (var command = c.Command(null, "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                var hits = new List<string>();
                foreach (var table in tables)
                {
                    var columns = new List<string>();
                    using (var info = c.Command(null, $"SELECT name FROM pragma_table_info('{table}') WHERE type = 'TEXT';"))
                    using (var reader = info.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            columns.Add(reader.GetString(0));
                        }
                    }

                    foreach (var column in columns)
                    {
                        var names = values.Select((_, i) => $"$v{i}").ToArray();
                        var parameters = values.Select((v, i) => ($"$v{i}", (object?)v)).ToArray();
                        if (c.Scalar<long>(null, $"SELECT count(*) FROM {table} WHERE {column} IN ({string.Join(", ", names)});", parameters) > 0)
                        {
                            hits.Add($"{table}.{column}");
                        }
                    }
                }

                return hits;
            },
            CancellationToken.None);

    private static Task<long> TotalRowsAsync(ChatHarness h) =>
        h.Database.ReadAsync(
            c => c.Scalar<long>(null, """
                SELECT (SELECT count(*) FROM conversation) + (SELECT count(*) FROM message) + (SELECT count(*) FROM generation_task)
                     + (SELECT count(*) FROM branch_source) + (SELECT count(*) FROM summary_node);
                """),
            CancellationToken.None);
}
