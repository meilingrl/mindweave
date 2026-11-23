using System.Globalization;
using Microsoft.Data.Sqlite;
using Mindweave.Application;
using Mindweave.Application.Ai;
using Mindweave.Application.Startup;
using Mindweave.Application.Storage;
using Mindweave.Application.Workspaces;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Reading;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Deleting a whole topic workspace (S7 round 4; user decision of 2026-11-02, departing from requirements §5.1) through
/// the real service and SQLite: a verified backup first, then everything of it in one transaction, nothing of any
/// other workspace touched; refused while work in it runs, while storage is paused, or when the backup fails.
/// </summary>
public class WorkspaceDeleteFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deleting_a_workspace_removes_every_row_of_it_and_leaves_every_other_row_as_it_was()
    {
        await using var h = await ChatHarness.StartAsync();
        var other = await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");
        await WorkspaceSeed.FillAsync(h, other);
        await WorkspaceSeed.FillAsync(h, h.Workspace);
        var inspected = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);
        Assert.Equal((true, 3, 2), (inspected.Exists, inspected.Conversations, inspected.Summaries));
        Assert.Empty(inspected.Busy);

        var ids = await WorkspaceSeed.IdsOfAsync(h.Database, h.Workspace);
        Assert.True(ids.Count > 30, $"the workspace, its conversations, messages, tasks, traces and summaries ({ids.Count})");
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(WorkspaceDeleteStatus.Deleted, outcome.Status);
        Assert.Equal(other, outcome.Next);
        Assert.Empty(await WorkspaceSeed.ReferencesAsync(h.Database, ids));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));

        // Exactly the rows that named something of the deleted workspace are gone; everything else is unchanged.
        var after = await WorkspaceSeed.SnapshotAsync(h.Database);
        foreach (var (table, rows) in before)
        {
            var kept = rows.Where(row => !ids.Any(id => row.Contains(id, StringComparison.Ordinal))).ToArray();
            Assert.True(kept.SequenceEqual(after[table]), $"{table}: {kept.Length} rows expected to stay, {after[table].Length} left");
        }

        Assert.Equal([other], (await h.Workspaces.ListAsync(Ct)).Select(w => w.Id));
        Assert.False((await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct)).Exists);
        Assert.False(h.Health.IsBlocked);
    }

    [Fact]
    public async Task The_backup_made_first_holds_the_whole_workspace_and_is_listed_for_restoring()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);

        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(WorkspaceDeleteStatus.Deleted, outcome.Status);
        Assert.Null(outcome.Next);
        var backup = Assert.Single(h.Database.ListBackups());
        Assert.Equal((outcome.BackupPath, BackupReason.BeforeWorkspaceDelete, Infrastructure.Sqlite.Migrations.MigrationCatalog.Default.LatestVersion), (backup.Path, backup.Reason, backup.SchemaVersion));
        using var copy = SqliteConnections.OpenReadOnly(backup.Path);
        Assert.Equal(1, copy.Scalar<long>(null, "SELECT count(*) FROM workspace WHERE id = $id;", ("$id", h.Workspace.ToString())));
        Assert.Equal(3, copy.Scalar<long>(null, "SELECT count(*) FROM conversation WHERE workspace_id = $id;", ("$id", h.Workspace.ToString())));
        Assert.True(copy.Scalar<long>(null, "SELECT count(*) FROM message WHERE conversation_id = $id;", ("$id", root.ToString())) >= 2);
        Assert.Equal("ok", SqliteConnections.QuickCheck(copy) ?? "ok");
    }

    [Fact]
    public async Task Deleting_is_refused_while_an_answer_or_a_summary_in_it_is_running_or_queued_and_changes_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var other = (await h.Conversations.StartAsync(h.Workspace, "蒸汽机车的锅炉为什么会爆炸？", Ct)).Conversation;
        await h.SettleAsync(other);
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(root, "再问一句", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        // The held text's one checkpoint must land before the snapshot; under load it could fall between the two.
        var answer = running.Answer!.Value.ToString();
        while (await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT revision FROM message WHERE id = $id;", ("$id", answer)), Ct) == 0)
        {
            await Task.Delay(10, Ct);
        }

        // A summary task row queued behind the UI's back.
        await h.Store.AddTaskAsync(
            new GenerationTaskRecord(GenerationTaskId.New(), GenerationTaskKind.Summary, other, null, (await h.Models.GetActiveSnapshotAsync(Ct))!, [], DateTimeOffset.UtcNow, 1),
            Ct);
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(WorkspaceDeleteStatus.Busy, outcome.Status);
        Assert.Equal(
            [new BusyConversation(root, Answer: true, Summary: false), new BusyConversation(other, Answer: false, Summary: true)],
            outcome.Busy.OrderBy(b => b.Answer ? 0 : 1));
        Assert.Equal(outcome.Busy.ToHashSet(), (await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct)).Busy.ToHashSet());
        Assert.Empty(h.Database.ListBackups());
        Assert.Equal(before, await WorkspaceSeed.SnapshotAsync(h.Database), new SnapshotComparer());
        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
    }

    [Fact]
    public async Task The_check_inside_the_transaction_refuses_work_that_started_after_the_first_look()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var held = h.Client.Hold("半句");
        var running = await h.Conversations.AskAsync(root, "再问一句", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var result = await h.Workspaces.DeleteAsync(h.Workspace, Ct);

        Assert.False(result.IsDeleted);
        Assert.Equal([new BusyConversation(root, true, false)], result.Busy);
        Assert.Equal(3, (await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct)).Conversations);
        held.Finish();
        await h.WaitForAnswerAsync(running.Answer!.Value);
    }

    [Fact]
    public async Task A_refusal_inside_the_transaction_leaves_no_backup_behind()
    {
        await using var h = await ChatHarness.StartAsync();
        var root = await WorkspaceSeed.FillAsync(h, h.Workspace);
        var held = h.Client.Hold("半句");
        Application.Conversations.SendOutcome? running = null;

        // The answer starts while the backup is being made: after the first look, before the transaction.
        var backup = new HookedBackup(h.Database, async () =>
        {
            running = await h.Conversations.AskAsync(root, "再问一句", Ct);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        });
        var deletion = new WorkspaceDeletionService(
            h.Workspaces, backup, h.Health, h.Generation, Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceDeletionService>.Instance);

        var outcome = await deletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(WorkspaceDeleteStatus.Busy, outcome.Status);
        Assert.Equal([new BusyConversation(root, true, false)], outcome.Busy);
        Assert.Null(outcome.BackupPath);
        Assert.NotNull(backup.Made);
        Assert.False(File.Exists(backup.Made));
        Assert.Empty(h.Database.ListBackups());
        held.Finish();
        await h.WaitForAnswerAsync(running!.Answer!.Value);
    }

    [Fact]
    public async Task Deleting_is_refused_while_storage_is_paused()
    {
        await using var h = await ChatHarness.StartAsync();
        await WorkspaceSeed.FillAsync(h, h.Workspace);
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        h.Health.Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct));

        Assert.Empty(h.Database.ListBackups());
        Assert.Equal(before, await WorkspaceSeed.SnapshotAsync(h.Database), new SnapshotComparer());
    }

    [Fact]
    public async Task When_the_backup_fails_nothing_is_deleted()
    {
        await using var h = await ChatHarness.StartAsync();
        await WorkspaceSeed.FillAsync(h, h.Workspace);
        var before = await WorkspaceSeed.SnapshotAsync(h.Database);

        // A file where the backup directory should be: the backup cannot be written.
        Directory.CreateDirectory(Path.GetDirectoryName(h.BackupDirectory)!);
        await File.WriteAllTextAsync(h.BackupDirectory, "not a directory", Ct);

        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(WorkspaceDeleteStatus.BackupFailed, outcome.Status);
        Assert.Equal(before, await WorkspaceSeed.SnapshotAsync(h.Database), new SnapshotComparer());
        Assert.False(h.Health.IsBlocked);
    }

    [Fact]
    public async Task The_next_workspace_is_the_one_used_most_recently()
    {
        await using var h = await ChatHarness.StartAsync();
        var older = await WorkspaceSeed.AddWorkspaceAsync(h, "较早");
        var recent = await WorkspaceSeed.AddWorkspaceAsync(h, "最近");
        var never = await WorkspaceSeed.AddWorkspaceAsync(h, "从未打开");
        var clock = new StepClock(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        var locations = new SqliteLocationStore(h.Database, clock);
        foreach (var workspace in new[] { older, recent, h.Workspace })
        {
            await locations.SetLastAsync(new PageLocation(workspace, "explore", null), Ct);
        }

        Assert.Equal(recent, (await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct)).Next);
        Assert.Null(await locations.GetLastAsync(Ct));
        Assert.Equal(older, (await h.WorkspaceDeletion.DeleteAsync(recent, Ct)).Next);
        Assert.Equal(never, (await h.WorkspaceDeletion.DeleteAsync(older, Ct)).Next);
        Assert.Null((await h.WorkspaceDeletion.DeleteAsync(never, Ct)).Next);
        Assert.Equal(WorkspaceDeleteStatus.Missing, (await h.WorkspaceDeletion.DeleteAsync(never, Ct)).Status);
    }

    [Fact]
    public async Task A_running_title_is_stopped_and_its_late_write_finds_nothing()
    {
        await using var h = await ChatHarness.StartAsync();
        var gate = h.Client.Titles.Hold();
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Client.Requests.Any(Testing.FakeChatClient.IsTitle));

        var outcome = await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct);

        Assert.Equal(WorkspaceDeleteStatus.Deleted, outcome.Status);
        Assert.Equal(0, h.Generation.UnfinishedCount);
        gate.TrySetResult();
        await ChatHarness.StaysAsync(() => !h.Health.IsBlocked, 100);
        Assert.Equal(0, await h.Database.ReadAsync(c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task;"), Ct));
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

    /// <summary>
    /// Moves one minute forward on every read, so each write gets a distinct, ordered time; kept apart from
    /// <see cref="Mindweave.Testing.ManualTime"/>, which only moves when told.
    /// </summary>
    private sealed class StepClock(DateTimeOffset start) : IClock
    {
        private DateTimeOffset _now = start;

        public DateTimeOffset UtcNow => _now = _now.AddMinutes(1);
    }

    private sealed class SnapshotComparer : IEqualityComparer<IReadOnlyDictionary<string, string[]>>
    {
        public bool Equals(IReadOnlyDictionary<string, string[]>? x, IReadOnlyDictionary<string, string[]>? y) =>
            x is not null && y is not null && x.Keys.SequenceEqual(y.Keys) && x.All(p => p.Value.SequenceEqual(y[p.Key]));

        public int GetHashCode(IReadOnlyDictionary<string, string[]> obj) => obj.Count;
    }
}

/// <summary>A workspace with something of every kind in it, for the workspace delete tests.</summary>
internal static class WorkspaceSeed
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<WorkspaceId> AddWorkspaceAsync(ChatHarness h, string name)
    {
        var workspace = new Workspace(WorkspaceId.New(), WorkspaceName.FromStored(name), DateTimeOffset.UtcNow);
        await h.Workspaces.AddAsync(workspace, Ct);
        return workspace.Id;
    }

    /// <summary>
    /// A root with an answer and a context summary; a branch with a failed answer, a retry and a regeneration, a draft
    /// and a reading position; an archived grandchild; summaries of the root (with a candidate, a read state and an edit
    /// draft) and of the branch, with saved node positions; and the last page pointing into the workspace. Returns the root.
    /// </summary>
    public static async Task<ConversationId> FillAsync(ChatHarness h, WorkspaceId workspace)
    {
        var sent = await h.Conversations.StartAsync(workspace, "火车是怎么发明的？", Ct);
        var root = sent.Conversation;
        var rootAnswer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(root);
        await h.Summaries.AddAsync(new ContextSummary(Guid.NewGuid(), new SummaryKey(root, 0, 1, "hash", 1), "fake-model", "早期铁路的背景", DateTimeOffset.UtcNow), Ct);

        var child = await h.BranchAsync(root, rootAnswer);
        h.Client.Fail(GenerationErrorKind.ServerError);
        var failed = await h.AskAsync(child, "子问");
        var retried = await h.RetryAsync(child, failed.Id);
        var regenerated = await h.RetryAsync(child, retried.Id);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        h.Reading.SetDraft(child, ViewerSlotKey.Primary, "没发出去的草稿");
        h.Reading.SetPosition(child, ViewerSlotKey.Primary, new ReadingPosition(regenerated.Id, 0, null, 1));
        await h.Reading.FlushAsync();

        var grandchild = await h.BranchAsync(child, regenerated);
        await h.SettleAsync(child);
        Assert.True((await h.Conversations.ArchiveAsync(grandchild, Ct)).Archived);

        var rootNode = await h.SummarizeAsync(root);
        var current = (await h.SummaryService.GetAsync(rootNode, Ct))!.Node.Current;
        await h.SummaryService.SetReadAsync(rootNode, current, read: true, Ct);
        await h.SummarizeAsync(root);
        await h.SummaryNodes.SaveDraftAsync(new SummaryEditDraft(rootNode, current, "改过的标题", "改过的正文", DateTimeOffset.UtcNow), Ct);
        var childNode = await h.SummarizeAsync(child);
        await h.KeepOldPositionAsync(workspace, rootNode);
        await h.KeepOldPositionAsync(workspace, childNode);
        await h.SummaryNetwork.SaveExplorationPlacesAsync(workspace, new Dictionary<ConversationId, ExplorationPlace> { [root] = new(10, 20, 0, 3) }, Ct);
        await h.SettleAsync(root);
        await h.SettleAsync(child);

        await new SqliteLocationStore(h.Database, new SystemClock()).SetLastAsync(new PageLocation(workspace, "explore", PageLocation.ConversationKey(child)), Ct);
        return root;
    }

    /// <summary>Every identity of the workspace: itself and every row id that belongs to it.</summary>
    public static Task<HashSet<string>> IdsOfAsync(SqliteDatabase database, WorkspaceId workspace) =>
        database.ReadAsync(
            c =>
            {
                var ids = new HashSet<string>(StringComparer.Ordinal) { workspace.ToString() };
                const string Conversations = "(SELECT id FROM conversation WHERE workspace_id = $ws)";
                foreach (var sql in new[]
                {
                    "SELECT id FROM conversation WHERE workspace_id = $ws;",
                    $"SELECT id FROM message WHERE conversation_id IN {Conversations};",
                    $"SELECT id FROM generation_task WHERE conversation_id IN {Conversations};",
                    $"SELECT id FROM ai_request WHERE task_id IN (SELECT id FROM generation_task WHERE conversation_id IN {Conversations});",
                    $"SELECT id FROM context_summary WHERE conversation_id IN {Conversations};",
                    "SELECT id FROM summary_node WHERE workspace_id = $ws;",
                    "SELECT id FROM summary_version WHERE node_id IN (SELECT id FROM summary_node WHERE workspace_id = $ws);",
                })
                {
                    using var command = c.Command(null, sql, ("$ws", workspace.ToString()));
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        ids.Add(reader.GetString(0));
                    }
                }

                return ids;
            },
            CancellationToken.None);

    /// <summary>Every "table.column" of text in the whole schema that still contains one of <paramref name="ids"/> anywhere in it.</summary>
    public static Task<List<string>> ReferencesAsync(SqliteDatabase database, HashSet<string> ids) =>
        database.ReadAsync(
            c =>
            {
                var hits = new List<string>();
                foreach (var table in Tables(c))
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
                        foreach (var id in ids)
                        {
                            if (c.Scalar<long>(null, $"SELECT count(*) FROM \"{table}\" WHERE instr(\"{column}\", $v) > 0;", ("$v", id)) > 0)
                            {
                                hits.Add($"{table}.{column} ({id})");
                            }
                        }
                    }
                }

                return hits;
            },
            CancellationToken.None);

    /// <summary>Every row of every table, rendered as text and sorted.</summary>
    public static Task<IReadOnlyDictionary<string, string[]>> SnapshotAsync(SqliteDatabase database) =>
        database.ReadAsync<IReadOnlyDictionary<string, string[]>>(
            c =>
            {
                var snapshot = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
                foreach (var table in Tables(c))
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
            CancellationToken.None);

    private static List<string> Tables(SqliteConnection c)
    {
        var tables = new List<string>();
        using var command = c.Command(null, "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }
}
