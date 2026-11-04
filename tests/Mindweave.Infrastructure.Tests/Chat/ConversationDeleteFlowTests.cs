using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Deleting conversations with a stated scope (requirements v0.12 §8.2, §9.5; architecture §9.1) through the real
/// service and SQLite: a conversation with all its branches (archived ones too) and the summaries of all of them, or a
/// selection of several; backed up first when branches or summaries go along; refused while user work in the scope
/// runs, when the scope changed after the confirmation, or when the backup fails; no row anywhere names what went.
/// </summary>
public class ConversationDeleteFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_scope_is_the_conversation_its_descendants_archived_ones_included_and_their_summaries()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var (child, grandchild) = await ChildrenAsync(h, root);

        var scope = await h.ConversationDeletion.InspectAsync([root], Ct);

        Assert.Equal([root], scope.Roots);
        Assert.Equal([root, child, grandchild], scope.Conversations);
        Assert.Equal(2, scope.Branches);
        Assert.Equal(2, scope.Summaries.Count);
        Assert.True(scope.IsCascade);
        Assert.Empty(scope.Busy);
        Assert.Empty(scope.ParentOf);
        Assert.Equal([root], scope.VisibleRoots);
        Assert.All(scope.Conversations, c => Assert.Equal(root, scope.RootOf[c]));

        // The archived grandchild alone: plain, hidden, its parent outside the scope.
        var leaf = await h.ConversationDeletion.InspectAsync([grandchild], Ct);
        Assert.Equal([grandchild], leaf.Conversations);
        Assert.Equal((0, 0, false), (leaf.Branches, leaf.Summaries.Count, leaf.IsCascade));
        Assert.Equal(child, leaf.ParentOf[grandchild]);
        Assert.Empty(leaf.VisibleRoots);
    }

    [Fact]
    public async Task Deleting_a_conversation_with_branches_and_summaries_removes_every_row_of_the_scope_and_nothing_else()
    {
        await using var h = await ChatHarness.StartAsync();
        var other = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var ids = await IdsOfScopeAsync(h, root);
        Assert.True(ids.Count > 30, $"the conversations, messages, tasks, traces, context summaries and summaries ({ids.Count})");
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        var outcome = await h.DeleteAsync(root);

        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
        Assert.Empty(await WorkspaceSeed.ReferencesAsync(h.Database, ids));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));

        // Exactly the rows naming something of the scope are gone; every other row (the other tree, the workspace,
        // the settings) is unchanged, except the last page, which pointed into the scope and is cleared.
        var after = await WorkspaceSeed.SnapshotAsync(h.Database);
        foreach (var (table, rows) in before.Where(t => t.Key != "workspace_location"))
        {
            var kept = rows.Where(row => !ids.Any(id => row.Contains(id, StringComparison.Ordinal))).ToArray();
            Assert.True(kept.SequenceEqual(after[table]), $"{table}: {kept.Length} rows expected to stay, {after[table].Length} left");
        }

        Assert.Null((await new SqliteLocationStore(h.Database, new SystemClock()).GetLastAsync(Ct))!.ContentKey);
        Assert.Equal(3, (await h.ConversationDeletion.InspectAsync([other], Ct)).Conversations.Count);
        Assert.Equal(2, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM summary_node;"), Ct));
        Assert.False(h.Health.IsBlocked);
    }

    [Fact]
    public async Task Deleting_a_branch_keeps_the_ancestor_and_a_sibling_and_the_context_summaries_they_share()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var (child, grandchild) = await ChildrenAsync(h, root);
        var rootAnswer = (await h.Store.GetMessagesAsync(root, Ct)).Last(m => m.Role == MessageRole.Assistant);
        var sibling = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(rootAnswer, "q"), Ct)).Child;

        // A context summary owned by the root (it covers the root's messages) that both branches reuse, and one owned by
        // the branch that goes.
        var shared = new ContextSummary(Guid.NewGuid(), new SummaryKey(root, 0, 2, "shared", 1), "fake-model", "共享的背景", DateTimeOffset.UtcNow);
        var own = new ContextSummary(Guid.NewGuid(), new SummaryKey(child, 0, 1, "own", 1), "fake-model", "分支自己的背景", DateTimeOffset.UtcNow);
        await h.Summaries.AddAsync(shared, Ct);
        await h.Summaries.AddAsync(own, Ct);
        var rootNode = (await h.SummaryService.ForConversationAsync(root, Ct))!.Node.Id;
        var ids = await IdsOfScopeAsync(h, child);
        Assert.DoesNotContain(root.ToString(), ids);

        var outcome = await h.DeleteAsync(child);

        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
        Assert.Equal([child, grandchild], outcome.Scope.Conversations);
        Assert.Equal(root, outcome.Scope.ParentOf[child]);
        Assert.Empty(await WorkspaceSeed.ReferencesAsync(h.Database, ids));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
        var contextIds = await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM context_summary WHERE id = $id;", ("$id", shared.Id.ToString())), Ct);
        Assert.Equal(1, contextIds);
        Assert.NotNull(await h.SummaryService.GetAsync(rootNode, Ct));
        Assert.Equal([sibling], (await h.Conversations.ListBranchesAsync(root, Ct)).Select(b => b.Child));
        Assert.NotNull(await h.Store.GetAsync(sibling, Ct));
    }

    [Fact]
    public async Task A_selection_of_a_parent_and_its_child_is_the_parent_scope_once()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var (child, grandchild) = await ChildrenAsync(h, first);
        var second = await WorkspaceSeed.FillAsync(h, h.Workspace);

        // The child selected before its parent, and the parent twice: the union counts each conversation once.
        var scope = await h.ConversationDeletion.InspectAsync([grandchild, child, first, second, first], Ct);

        Assert.Equal([first, second], scope.Roots);
        Assert.Equal(6, scope.Conversations.Count);
        Assert.Equal(6, scope.Conversations.Distinct().Count());
        Assert.Equal(4, scope.Branches);
        Assert.Equal(4, scope.Summaries.Count);

        var outcome = await h.ConversationDeletion.DeleteAsync(scope, Ct);

        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
        Assert.Empty(await h.Conversations.ListAsync(h.Workspace, Ct));
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM summary_node;"), Ct));
        Assert.Single(h.Database.ListBackups());
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
    }

    [Fact]
    public async Task A_cascading_delete_is_backed_up_first_and_the_backup_holds_the_whole_scope()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);

        var outcome = await h.DeleteAsync(root);

        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
        var backup = Assert.Single(h.Database.ListBackups());
        Assert.Equal((outcome.BackupPath, BackupReason.BeforeConversationDelete), (backup.Path, backup.Reason));
        Assert.EndsWith("-before-conversation-delete.db", backup.Path, StringComparison.Ordinal);
        using var copy = SqliteConnections.OpenReadOnly(backup.Path);
        Assert.Equal(3, copy.Scalar<long>(null, "SELECT count(*) FROM conversation;"));
        Assert.Equal(2, copy.Scalar<long>(null, "SELECT count(*) FROM summary_node;"));
    }

    [Fact]
    public async Task A_plain_delete_needs_no_backup()
    {
        await using var h = await ChatHarness.StartAsync();
        var a = (await h.Conversations.StartAsync(h.Workspace, "一", Ct)).Conversation;
        var b = (await h.Conversations.StartAsync(h.Workspace, "二", Ct)).Conversation;
        await h.SettleAsync(a);
        await h.SettleAsync(b);

        var scope = await h.ConversationDeletion.InspectAsync([a, b], Ct);
        Assert.False(scope.IsCascade);
        var outcome = await h.ConversationDeletion.DeleteAsync(scope, Ct);

        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
        Assert.Null(outcome.BackupPath);
        Assert.Empty(h.Database.ListBackups());
        Assert.Empty(await h.Conversations.ListAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task When_the_backup_fails_nothing_is_deleted()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);
        Directory.CreateDirectory(Path.GetDirectoryName(h.BackupDirectory)!);
        await File.WriteAllTextAsync(h.BackupDirectory, "not a directory", Ct);

        var outcome = await h.DeleteAsync(root);

        Assert.Equal(ConversationDeleteStatus.BackupFailed, outcome.Status);
        AssertSame(before, await WorkspaceSeed.SnapshotAsync(h.Database));
        Assert.False(h.Health.IsBlocked);
    }

    [Fact]
    public async Task Deleting_is_refused_while_a_user_answer_or_summary_in_the_scope_runs_and_nothing_is_backed_up()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var (child, grandchild) = await ChildrenAsync(h, root);
        await h.Conversations.RestoreAsync(grandchild, Ct);
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(grandchild, "再问一句", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await h.Store.AddTaskAsync(
            new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Summary, child, null, (await h.Models.GetActiveSnapshotAsync(Ct))!, [], DateTimeOffset.UtcNow, 1),
            Ct);
        await UntilRevisionAsync(h, running.Answer!.Value);
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        var outcome = await h.DeleteAsync(root);

        Assert.Equal(ConversationDeleteStatus.Busy, outcome.Status);
        Assert.Equal(
            new HashSet<BusyConversation> { new(child, Answer: false, Summary: true), new(grandchild, Answer: true, Summary: false) },
            outcome.Scope.Busy.ToHashSet());
        Assert.Empty(h.Database.ListBackups());
        AssertSame(before, await WorkspaceSeed.SnapshotAsync(h.Database));
        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
    }

    [Fact]
    public async Task Work_that_starts_after_the_backup_is_refused_inside_the_transaction_and_the_backup_is_discarded()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var held = h.Client.Hold("半句");
        Application.Conversations.SendOutcome? running = null;
        var backup = new HookedBackup(h.Database, async () =>
        {
            running = await h.Conversations.AskAsync(root, "再问一句", Ct);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        });
        var deletion = Deletion(h, backup);
        var scope = await deletion.InspectAsync([root], Ct);

        var outcome = await deletion.DeleteAsync(scope, Ct);

        Assert.Equal(ConversationDeleteStatus.Busy, outcome.Status);
        Assert.Equal([new BusyConversation(root, true, false)], outcome.Scope.Busy);
        Assert.NotNull(backup.Made);
        Assert.False(File.Exists(backup.Made));
        Assert.Empty(h.Database.ListBackups());
        Assert.Equal(3, (await h.ConversationDeletion.InspectAsync([root], Ct)).Conversations.Count);
        held.Finish();
        await h.WaitForAnswerAsync(running!.Answer!.Value);
    }

    [Fact]
    public async Task A_scope_that_changed_after_the_confirmation_is_refused_and_returned_for_confirming_again()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var confirmed = await h.ConversationDeletion.InspectAsync([root], Ct);

        // A new branch after the confirmation was shown: deleting would take more than the user saw.
        var rootAnswer = (await h.Store.GetMessagesAsync(root, Ct)).Last(m => m.Role == MessageRole.Assistant);
        var late = (await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(rootAnswer, "q"), Ct)).Child;
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        var outcome = await h.ConversationDeletion.DeleteAsync(confirmed, Ct);

        Assert.Equal(ConversationDeleteStatus.ScopeChanged, outcome.Status);
        Assert.Contains(late, outcome.Scope.Conversations);
        Assert.Equal(confirmed.Conversations.Count + 1, outcome.Scope.Conversations.Count);
        Assert.Empty(h.Database.ListBackups());
        AssertSame(before, await WorkspaceSeed.SnapshotAsync(h.Database));

        // Confirmed again with the new scope, it goes.
        Assert.Equal(ConversationDeleteStatus.Deleted, (await h.ConversationDeletion.DeleteAsync(outcome.Scope, Ct)).Status);
    }

    [Fact]
    public async Task A_scope_that_changes_after_the_backup_is_refused_inside_the_transaction_and_the_backup_is_discarded()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var rootAnswer = (await h.Store.GetMessagesAsync(root, Ct)).Last(m => m.Role == MessageRole.Assistant);
        var backup = new HookedBackup(h.Database, async () =>
            Assert.True((await h.Conversations.CreateBranchAsync(root, BranchFlowTests.Selection(rootAnswer, "q"), Ct)).Accepted));
        var deletion = Deletion(h, backup);
        var scope = await deletion.InspectAsync([root], Ct);

        var outcome = await deletion.DeleteAsync(scope, Ct);

        Assert.Equal(ConversationDeleteStatus.ScopeChanged, outcome.Status);
        Assert.Equal(4, outcome.Scope.Conversations.Count);
        Assert.False(File.Exists(backup.Made));
        Assert.Equal(4, (await h.ConversationDeletion.InspectAsync([root], Ct)).Conversations.Count);
    }

    [Fact]
    public async Task Deleting_is_refused_while_storage_is_paused_and_a_gone_selection_is_missing()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var scope = await h.ConversationDeletion.InspectAsync([root], Ct);

        h.Health.Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.ConversationDeletion.DeleteAsync(scope, Ct));
        Assert.Empty(h.Database.ListBackups());
        await UntilAsync(() => !h.Health.IsBlocked);

        Assert.Equal(ConversationDeleteStatus.Deleted, (await h.ConversationDeletion.DeleteAsync(scope, Ct)).Status);
        Assert.Equal(ConversationDeleteStatus.Missing, (await h.ConversationDeletion.DeleteAsync(scope, Ct)).Status);
    }

    [Fact]
    public async Task Background_titles_in_the_scope_are_stopped_and_their_late_writes_find_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.HoldTitles();
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(Testing.FakeChatClient.IsTitle));
        var child = (await h.Conversations.CreateBranchAsync(sent.Conversation, BranchFlowTests.Selection(answer, "q"), Ct)).Child;

        var outcome = await h.DeleteAsync(sent.Conversation);

        Assert.Equal(ConversationDeleteStatus.Deleted, outcome.Status);
        Assert.Equal([sent.Conversation, child], outcome.Scope.Conversations);
        Assert.Equal(0, h.Generation.UnfinishedCount);
        gate.TrySetResult();
        await Task.Delay(100, Ct);
        Assert.False(h.Health.IsBlocked);
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task;"), Ct));
    }

    [Fact]
    public async Task Every_deleted_conversation_is_announced_and_the_parent_hears_its_branch_list_changed()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var (child, grandchild) = await ChildrenAsync(h, root);
        var deleted = new List<ConversationId>();
        var changed = new List<ConversationId>();
        IReadOnlyList<ConversationId>? hidden = null;
        h.Conversations.Deleted += (_, id) => deleted.Add(id);
        h.Conversations.ConversationChanged += (_, id) => changed.Add(id);
        h.Conversations.VisibilityChanged += (_, ids) => hidden = ids;

        Assert.True((await h.DeleteAsync(child)).IsDeleted);

        Assert.Equal([child, grandchild], deleted);
        Assert.Contains(root, changed);
        Assert.Equal([child, grandchild], hidden);
    }

    private static Application.Conversations.ConversationDeletionService Deletion(ChatHarness h, IDatabaseBackup backup) =>
        new(h.Archive, backup, h.Health, h.Generation, h.Conversations, Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Conversations.ConversationDeletionService>.Instance);

    /// <summary>The branch and the archived grandchild <see cref="WorkspaceSeed.FillAsync"/> made under <paramref name="root"/>.</summary>
    private static async Task<(ConversationId Child, ConversationId Grandchild)> ChildrenAsync(ChatHarness h, ConversationId root)
    {
        var child = Assert.Single(await h.Conversations.ListBranchesAsync(root, Ct)).Child;
        var grandchild = Assert.Single(await h.Conversations.ListBranchesAsync(child, Ct)).Child;
        return (child, grandchild);
    }

    /// <summary>Every identity of the scope of <paramref name="root"/>: its conversations and every row id that belongs to them.</summary>
    private static async Task<HashSet<string>> IdsOfScopeAsync(ChatHarness h, ConversationId root)
    {
        var scope = await h.ConversationDeletion.InspectAsync([root], Ct);
        return await h.Database.ReadAsync(
            c =>
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var conversation in scope.Conversations)
                {
                    ids.Add(conversation.ToString());
                    foreach (var sql in new[]
                    {
                        "SELECT id FROM message WHERE conversation_id = $id;",
                        "SELECT id FROM generation_task WHERE conversation_id = $id;",
                        "SELECT id FROM ai_request WHERE task_id IN (SELECT id FROM generation_task WHERE conversation_id = $id);",
                        "SELECT id FROM context_summary WHERE conversation_id = $id;",
                        "SELECT id FROM summary_node WHERE conversation_id = $id;",
                        "SELECT id FROM summary_version WHERE node_id IN (SELECT id FROM summary_node WHERE conversation_id = $id);",
                    })
                    {
                        using var command = c.Command(null, sql, ("$id", conversation.ToString()));
                        using var reader = command.ExecuteReader();
                        while (reader.Read())
                        {
                            ids.Add(reader.GetString(0));
                        }
                    }
                }

                return ids;
            },
            Ct);
    }

    private static async Task UntilRevisionAsync(ChatHarness h, MessageId answer)
    {
        while (await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT revision FROM message WHERE id = $id;", ("$id", answer.ToString())), Ct) == 0)
        {
            await Task.Delay(10, Ct);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached");
            await Task.Delay(20, Ct);
        }
    }

    private static void AssertSame(IReadOnlyDictionary<string, string[]> before, IReadOnlyDictionary<string, string[]> after)
    {
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (table, rows) in before)
        {
            Assert.True(rows.SequenceEqual(after[table]), $"{table} changed");
        }
    }

    /// <summary>The real backup, with something run after the copy is made.</summary>
    private sealed class HookedBackup(IDatabaseBackup inner, Func<Task> after) : IDatabaseBackup
    {
        public string? Made { get; private set; }

        public async Task<string> BackupAsync(BackupReason reason, CancellationToken cancellationToken)
        {
            Made = await inner.BackupAsync(reason, cancellationToken);
            await after();
            return Made;
        }

        public void Discard(string path) => inner.Discard(path);
    }
}
