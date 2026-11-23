using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Tests.Chat;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// The process is killed inside a multi-table runtime write, after its statements ran and before COMMIT (architecture
/// §12.1–12.2, requirements §9.3). On the next start the database opens normally, passes the integrity and foreign key
/// checks, holds exactly what it held before (every row of every table compared), and the same operation then
/// succeeds. The child process runs the write through the application's own store; a test-only hook on the writer
/// blocks it before COMMIT. Repeated in the stress workflow (filter <c>Category=FaultInjection</c>).
/// </summary>
[Trait("Category", "FaultInjection")]
public sealed class TransactionKillTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Killing_the_process_while_a_branch_is_created_leaves_no_part_of_it()
    {
        ConversationId parent;
        MessageId source;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            var (root, rootAnswer) = await h.RootWithAnswerAsync("根");
            parent = await h.BranchAsync(root, rootAnswer);
            source = (await h.AskAsync(parent, "子问")).Id;
            await h.SettleAsync(parent);
        }

        var child = ConversationId.New();
        var before = await SnapshotAsync();

        // The conversation, its source and both ancestor ranges (parent and root) are written, not committed.
        await KillInsideAsync(4, "branch", parent.ToString(), source.ToString(), child.ToString());

        await using var database = await ReopenAsync(before);
        var branches = new SqliteBranchStore(database);
        await branches.CreateAsync(parent, child, BranchFlowTests.Selection((await new SqliteConversationStore(database).GetMessagesAsync(parent, Ct)).Single(m => m.Id == source), "q"), DateTimeOffset.UtcNow, Ct);
        Assert.Equal(parent, (await branches.GetSourceAsync(child, Ct))!.Parent);
        Assert.Equal(2, await CountAsync(database, "SELECT count(*) FROM branch_context_range WHERE child_id = $id;", ("$id", child.ToString())));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_a_candidate_is_adopted_keeps_the_old_current_version()
    {
        SummaryNodeId node;
        SummaryVersionId current, candidate;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            var (conversation, _) = await h.RootWithAnswerAsync("火车是怎么发明的？");
            node = await h.SummarizeAsync(conversation);
            current = (await h.SummaryService.GetAsync(node, Ct))!.Node.Current;
            await h.SummaryService.SetReadAsync(node, current, read: true, Ct);
            await h.SummarizeAsync(conversation);
            candidate = (await h.SummaryService.GetAsync(node, Ct))!.Node.Pending!.Value;
            await h.SettleAsync(conversation);
        }

        var before = await SnapshotAsync();

        await KillInsideAsync(1, "adopt", node.ToString(), candidate.ToString());

        await using var database = await ReopenAsync(before);
        var summaries = new SqliteSummaryStore(database);
        var kept = (await summaries.GetAsync(node, Ct))!.Node;
        Assert.Equal((current, candidate), (kept.Current, kept.Pending));
        await summaries.AdoptAsync(node, candidate, DateTimeOffset.UtcNow, Ct);
        var adopted = (await summaries.GetAsync(node, Ct))!.Node;
        Assert.Equal((candidate, (SummaryVersionId?)null), (adopted.Current, adopted.Pending));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_versions_are_deleted_keeps_every_version_and_its_read_state()
    {
        SummaryNodeId node;
        SummaryVersionId first, second, third;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            var (conversation, _) = await h.RootWithAnswerAsync("火车是怎么发明的？");
            node = await h.SummarizeAsync(conversation);
            first = (await h.SummaryService.GetAsync(node, Ct))!.Node.Current;
            second = await SummarizeAndAdoptAsync(h, conversation, node);
            third = await SummarizeAndAdoptAsync(h, conversation, node);
            foreach (var version in new[] { first, second, third })
            {
                await h.SummaryService.SetReadAsync(node, version, read: true, Ct);
            }

            await h.SettleAsync(conversation);
        }

        var before = await SnapshotAsync();
        Assert.Equal(3, await CountAsync(_dir, "SELECT count(*) FROM summary_read_state;"));

        // The node moves to the second version and the first and current ones go (their read state cascades).
        await KillInsideAsync(5, "delete-versions", node.ToString(), first.ToString(), third.ToString());

        await using var database = await ReopenAsync(before);
        var summaries = new SqliteSummaryStore(database);
        Assert.Equal(third, (await summaries.GetAsync(node, Ct))!.Node.Current);
        var deleted = await summaries.DeleteVersionsAsync(node, new HashSet<SummaryVersionId> { first, third }, DateTimeOffset.UtcNow, Ct);
        Assert.Equal(second, deleted.NewCurrent);
        Assert.Equal([second], (await summaries.GetAsync(node, Ct))!.Versions.Select(v => v.Id));
        Assert.Equal(1, await CountAsync(database, "SELECT count(*) FROM summary_read_state;"));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_a_subtree_is_archived_leaves_it_visible_and_its_queued_titles_queued()
    {
        ConversationId root, child, grandchild;
        GenerationTaskId title, naming;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            Message rootAnswer;
            (root, rootAnswer) = await h.RootWithAnswerAsync("根");
            child = await h.BranchAsync(root, rootAnswer);
            var childAnswer = await h.AskAsync(child, "子问");
            grandchild = await h.BranchAsync(child, childAnswer);
            await h.SettleAsync(child);

            // Queued behind the coordinator's back, so they are still queued when the archive runs (MW-C14).
            var model = (await h.Models.GetActiveSnapshotAsync(Ct))!;
            title = GenerationTaskId.New();
            naming = GenerationTaskId.New();
            await h.Store.AddTaskAsync(new GenerationTaskRecord(title, GenerationTaskKind.Title, child, null, model, [], DateTimeOffset.UtcNow), Ct);
            await h.Store.AddTaskAsync(new GenerationTaskRecord(naming, GenerationTaskKind.Naming, grandchild, null, model, [], DateTimeOffset.UtcNow), Ct);
        }

        var before = await SnapshotAsync();

        // The archive row and both cancellations are written, not committed.
        await KillInsideAsync(3, "archive", root.ToString());

        await using var database = await ReopenAsync(before, beforeStartup: async d =>
        {
            Assert.Equal(0, await CountAsync(d, "SELECT count(*) FROM conversation_archive;"));
            Assert.Equal(2, await CountAsync(d, "SELECT count(*) FROM generation_task WHERE id IN ($a, $b) AND state = 'queued';", ("$a", title.ToString()), ("$b", naming.ToString())));
        });
        var archive = new SqliteArchiveStore(database);
        var result = await archive.ArchiveAsync(root, DateTimeOffset.UtcNow, Ct);
        Assert.Empty(result.Busy);
        Assert.Equal(new HashSet<ConversationId> { root, child, grandchild }, result.Subtree.ToHashSet());
        Assert.Equal([root], await archive.ArchivedAmongAsync([root, child, grandchild], Ct));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_a_deep_restore_runs_keeps_every_archive_mark()
    {
        ConversationId root, child, grandchild;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            Message rootAnswer;
            (root, rootAnswer) = await h.RootWithAnswerAsync("根");
            child = await h.BranchAsync(root, rootAnswer);
            var childAnswer = await h.AskAsync(child, "子问");
            grandchild = await h.BranchAsync(child, childAnswer);
            await h.SettleAsync(child);
            Assert.True((await h.Conversations.ArchiveAsync(child, Ct)).Archived);
            Assert.True((await h.Conversations.ArchiveAsync(root, Ct)).Archived);
        }

        var before = await SnapshotAsync();

        // Restoring the hidden grandchild clears both archived ancestors on its path.
        await KillInsideAsync(2, "restore", grandchild.ToString());

        await using var database = await ReopenAsync(before);
        var archive = new SqliteArchiveStore(database);
        Assert.Equal(new HashSet<ConversationId> { root, child }, (await archive.ArchivedAmongAsync([root, child, grandchild], Ct)).ToHashSet());
        var shown = await archive.RestoreAsync(grandchild, Ct);
        Assert.Equal(new HashSet<ConversationId> { root, child, grandchild }, shown.ToHashSet());
        Assert.Empty(await archive.ArchivedAmongAsync([root, child, grandchild], Ct));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_a_conversation_is_deleted_keeps_all_of_it()
    {
        ConversationId root, child;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            Message rootAnswer;
            (root, rootAnswer) = await h.RootWithAnswerAsync("根");
            child = await h.BranchAsync(root, rootAnswer);
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
        }

        var before = await SnapshotAsync();
        Assert.True(await CountAsync(_dir, "SELECT count(*) FROM message WHERE conversation_id = $id AND supersedes_id IS NOT NULL;", ("$id", child.ToString())) >= 2);

        // Task rows, the source row, the retry links, the messages, the last location and the conversation itself.
        await KillInsideAsync(25, "delete", child.ToString());

        await using var database = await ReopenAsync(before);
        Assert.Equal(PageLocation.ConversationKey(child), (await new SqliteLocationStore(database, new SystemClock()).GetLastAsync(Ct))!.ContentKey);
        var archive = new SqliteArchiveStore(database);
        var result = await archive.DeleteAsync(await archive.InspectDeleteAsync([child], Ct), Ct);
        Assert.Equal((ConversationDeleteStatus.Deleted, root), (result.Status, result.Scope.ParentOf[child]));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM message WHERE conversation_id = $id;", ("$id", child.ToString())));
        Assert.Null(await new SqliteConversationStore(database).GetAsync(child, Ct));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_a_conversation_is_deleted_with_its_branches_and_summaries_keeps_all_of_it()
    {
        ConversationId root;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        }

        var before = await SnapshotAsync();

        // The summary nodes with their old positions, drafts and versions, the last page, then every row of the three
        // conversations: the exploration's place, tasks, sources and ranges, retry links, messages and the conversations.
        await KillInsideAsync(CascadeDeleteChanges, "delete", root.ToString());

        await using var database = await ReopenAsync(before);
        var archive = new SqliteArchiveStore(database);
        var scope = await archive.InspectDeleteAsync([root], Ct);
        Assert.Equal((1, 2, 2), (scope.Roots.Count, scope.Branches, scope.Summaries.Count));
        Assert.Equal(ConversationDeleteStatus.Deleted, (await archive.DeleteAsync(scope, Ct)).Status);
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM conversation;"));
        Assert.Equal(0, await CountAsync(database, "SELECT count(*) FROM summary_node;"));
        await StorageFaults.AssertIntactAsync(database);
    }

    /// <summary>The workspace delete's 59 for the same rows, less its workspace, last-workspace marker and location rows, plus one cleared last page.</summary>
    private const int CascadeDeleteChanges = 57;

    [Fact]
    public async Task Killing_the_process_while_a_workspace_is_deleted_keeps_all_of_it()
    {
        Core.Workspaces.WorkspaceId workspace, other;
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            workspace = h.Workspace;
            other = await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");
            await WorkspaceSeed.FillAsync(h, other);
            await WorkspaceSeed.FillAsync(h, workspace);
        }

        var before = await SnapshotAsync();

        // Every row of the workspace: its summaries, old positions and exploration place, tasks, sources and ranges, retry links, messages,
        // conversations, last page, last-workspace marker and the workspace itself.
        await KillInsideAsync(59, "delete-workspace", workspace.ToString());

        await using var database = await ReopenAsync(before);
        var store = new SqliteWorkspaceStore(database);
        var kept = await store.InspectAsync(workspace, Ct);
        Assert.Equal((true, 3, 2), (kept.Exists, kept.Conversations, kept.Summaries));
        var result = await store.DeleteAsync(workspace, Ct);
        Assert.True(result.IsDeleted);
        Assert.Equal(other, result.Next);
        Assert.Equal([other], (await store.ListAsync(Ct)).Select(w => w.Id));
        await StorageFaults.AssertIntactAsync(database);
    }

    [Fact]
    public async Task Killing_the_process_while_exploration_places_are_saved_keeps_the_previous_places()
    {
        // Enough rows that the save outgrows the page cache and writes uncommitted pages to the WAL.
        const int Explorations = 600;
        Core.Workspaces.WorkspaceId workspace;
        var roots = new List<ConversationId>();
        await using (var h = await ChatHarness.StartAsync(directory: _dir))
        {
            workspace = h.Workspace;
            var placed = new Dictionary<ConversationId, Application.Storage.ExplorationPlace>();
            for (var i = 0; i < Explorations; i++)
            {
                roots.Add(await new Testing.Usage.UsageSeed(h.Database).ConversationAsync(workspace, DateTimeOffset.UtcNow));
                if (i % 4 == 0)
                {
                    placed[roots[^1]] = new(-1 - i, -1 - i, 0, 1);
                }
            }

            // A quarter already has a saved place: the save below both updates and inserts.
            await h.SummaryNetwork.SaveExplorationPlacesAsync(workspace, placed, Ct);
        }

        var before = await SnapshotAsync();

        await KillInsideAsync(Explorations, "places", workspace.ToString());

        await using var database = await ReopenAsync(before);
        var network = new SqliteSummaryNetworkStore(database);
        var places = roots.OrderBy(r => r.ToString(), StringComparer.Ordinal).Select((r, i) => (r, i)).ToDictionary(p => p.r, p => new Application.Storage.ExplorationPlace(p.i * 10, p.i * 20, 0, 1));
        Assert.Equal(Explorations, await network.SaveExplorationPlacesAsync(workspace, places, DateTimeOffset.UtcNow, Ct));
        Assert.Equal(places, await network.GetExplorationPlacesAsync(workspace, Ct));
        await StorageFaults.AssertIntactAsync(database);
    }

    /// <summary>
    /// Runs the operation in a child process, waits (bounded) until it stops before COMMIT, checks it made exactly
    /// <paramref name="changes"/> row changes in that transaction — the whole operation, so an operation split into
    /// several transactions (whose first part would stop here with fewer) fails — and kills it there. Returns how far the WAL grew meanwhile.
    /// </summary>
    private async Task<long> KillInsideAsync(int changes, string operation, params string[] ids)
    {
        using var child = ChildProcess.Start("hang-before-commit", _dir.Paths.DataDirectory, [operation, .. ids]);
        var line = await child.WaitForLineAsync(TimeSpan.FromSeconds(60));
        await child.KillAsync();
        var parts = line.Split(' ');
        Assert.Equal("in-transaction", parts[0]);
        Assert.True(long.Parse(parts[1], CultureInfo.InvariantCulture) == changes, $"{operation} made {parts[1]} changes before COMMIT, expected exactly {changes} (the whole operation in one transaction)");
        return long.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Opens the data directory as the application does after a crash (the open itself waits out a WAL index a killed
    /// process still maps on Windows), checks it and compares every row with <paramref name="before"/>, then runs
    /// startup recovery as the application would before handing the database to the test.
    /// </summary>
    private async Task<SqliteDatabase> ReopenAsync(IReadOnlyDictionary<string, string[]> before, Func<SqliteDatabase, Task>? beforeStartup = null)
    {
        var database = _dir.Database();
        try
        {
            var opened = await database.OpenAsync(Ct);
            Assert.True(opened.IsReady, $"{opened.Status}: {opened.ErrorDetail}");
            await StorageFaults.AssertIntactAsync(database);
            var after = await SnapshotAsync(database);
            Assert.Equal(before.Keys, after.Keys);
            foreach (var (table, rows) in before)
            {
                Assert.True(rows.SequenceEqual(after[table]), $"{table} changed: {rows.Length} rows before, {after[table].Length} after");
            }

            if (beforeStartup is not null)
            {
                await beforeStartup(database);
            }

            var store = new SqliteConversationStore(database);
            var startup = new StartupCoordinator(database, new SqliteWorkspaceStore(database), store, new SystemClock(), new SqliteLocationStore(database, new SystemClock()), NullLogger<StartupCoordinator>.Instance);
            var outcome = await startup.StartAsync(Ct);
            Assert.True(outcome is StartupOutcome.ReadyToWork, outcome is StartupOutcome.NeedsRecovery r ? $"{r.Result.Status}: {r.Result.ErrorDetail}" : outcome.ToString());
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, string[]>> SnapshotAsync()
    {
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        return await SnapshotAsync(database);
    }

    /// <summary>Every row of every table, each rendered as text and sorted, so any difference at all shows.</summary>
    private static Task<IReadOnlyDictionary<string, string[]>> SnapshotAsync(SqliteDatabase database) =>
        database.ReadAsync<IReadOnlyDictionary<string, string[]>>(
            c =>
            {
                var tables = new List<string>();
                using (var command = c.Command(null, "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                var snapshot = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var table in tables)
                {
                    using var command = c.Command(null, $"SELECT * FROM \"{table}\";");
                    using var reader = command.ExecuteReader();
                    var rows = new List<string>();
                    while (reader.Read())
                    {
                        var values = new string[reader.FieldCount];
                        for (var i = 0; i < values.Length; i++)
                        {
                            values[i] = reader.GetValue(i) switch
                            {
                                DBNull => "∅",
                                byte[] bytes => Convert.ToHexString(bytes),
                                var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
                            };
                        }

                        rows.Add(string.Join('|', values));
                    }

                    snapshot[table] = [.. rows.Order(StringComparer.Ordinal)];
                }

                return snapshot;
            },
            Ct);

    private static Task<long> CountAsync(SqliteDatabase database, string sql, params (string Name, object? Value)[] parameters) =>
        database.ReadAsync(c => c.Scalar<long>(null, sql, parameters), Ct);

    private static async Task<long> CountAsync(TempDataDirectory dir, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var database = dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        return await CountAsync(database, sql, parameters);
    }

    private static async Task<SummaryVersionId> SummarizeAndAdoptAsync(ChatHarness h, ConversationId conversation, SummaryNodeId node)
    {
        await h.SummarizeAsync(conversation);
        var candidate = (await h.SummaryService.GetAsync(node, Ct))!.Node.Pending!.Value;
        await h.SummaryService.AdoptAsync(node, candidate, Ct);
        return candidate;
    }
}
