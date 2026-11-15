using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Export;
using Mindweave.Application.Storage;
using Mindweave.Application.SummaryLinks;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Export;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// User-made content links between two summaries (requirements §10.3.3, 2026-11-16) through the real service and SQLite:
/// one undirected link per pair in one workspace with an optional note of at most 16 characters; removed with either
/// summary by every deletion path and counted in each confirmation's scope; a deleted link's pair recorded as ignored;
/// kept by archiving; left out of the Markdown export, conversation context and every summary count.
/// </summary>
public class SummaryLinkFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_link_is_one_undirected_pair_with_a_short_note_and_the_service_refuses_what_the_requirements_rule_out()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, a) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var (_, b) = await SummarizedRootAsync(h, "铁轨用什么做？");
        var (_, c) = await SummarizedRootAsync(h, "高铁为什么快？");
        var changed = new List<Core.Workspaces.WorkspaceId>();
        h.Links.Changed += (_, ws) => changed.Add(ws);

        // Made from b to a, stored in the normalized order; the note is trimmed.
        var link = await h.Links.CreateAsync(b, a, "  对比  ", Ct);
        var (first, second) = SummaryLinkPair.Normalize(a, b);
        Assert.Equal((first, second, "对比", SummaryLinkOrigin.User, h.Workspace), (link.NodeA, link.NodeB, link.Note, link.Origin, link.Workspace));
        Assert.True(string.CompareOrdinal(link.NodeA.ToString(), link.NodeB.ToString()) < 0);
        Assert.Equal([h.Workspace], changed);

        // Read back from either end with the other summary's name; listed for the workspace once.
        var fromA = Assert.Single(await h.Links.ListForNodeAsync(a, Ct));
        Assert.Equal((b, link.Id), (fromA.Other, fromA.Link.Id));
        Assert.Equal((await h.SummaryService.GetAsync(b, Ct))!.Node.DisplayName, fromA.OtherName);
        Assert.Equal(a, Assert.Single(await h.Links.ListForNodeAsync(b, Ct)).Other);
        Assert.Equal([link.Id], (await h.Links.ListAsync(h.Workspace, Ct)).Select(l => l.Id));

        // The picker offers the other summaries, the linked one marked.
        var candidates = await h.Links.CandidatesAsync(a, Ct);
        Assert.Equal(new HashSet<SummaryNodeId> { b, c }, candidates.Select(x => x.Node).ToHashSet());
        Assert.True(candidates.Single(x => x.Node == b).Linked);
        Assert.False(candidates.Single(x => x.Node == c).Linked);

        // Refused: the same pair either way round, itself, a note over 16 characters; nothing more is stored.
        Assert.Equal(SummaryLinkRefusal.Duplicate, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.CreateAsync(a, b, null, Ct))).Reason);
        Assert.Equal(SummaryLinkRefusal.Duplicate, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.CreateAsync(b, a, "又一次", Ct))).Reason);
        Assert.Equal(SummaryLinkRefusal.Self, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.CreateAsync(c, c, null, Ct))).Reason);
        Assert.Equal(SummaryLinkRefusal.NoteTooLong, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.CreateAsync(a, c, new string('长', 17), Ct))).Reason);
        Assert.Equal(1, await h.CountAsync("summary_link"));

        // Exactly 16 characters (after trimming) is allowed; an empty note is none.
        var full = await h.Links.CreateAsync(a, c, " " + new string('例', 16) + " ", Ct);
        Assert.Equal(new string('例', 16), full.Note);
        var none = await h.Links.EditNoteAsync(full.Id, "   ", Ct);
        Assert.Null(none.Note);
        Assert.Equal("原因", (await h.Links.EditNoteAsync(full.Id, "原因", Ct)).Note);
        Assert.Equal(SummaryLinkRefusal.NoteTooLong, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.EditNoteAsync(full.Id, new string('长', 17), Ct))).Reason);
        Assert.Equal("原因", (await h.Links.ListForNodeAsync(c, Ct)).Single().Link.Note);

        // A summary that is gone, and a link that is gone, are refused as missing.
        Assert.Equal(SummaryLinkRefusal.Missing, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.CreateAsync(a, SummaryNodeId.New(), null, Ct))).Reason);
        Assert.Equal(SummaryLinkRefusal.Missing, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.EditNoteAsync(SummaryLinkId.New(), "x", Ct))).Reason);
        Assert.False(await h.Links.DeleteAsync(SummaryLinkId.New(), Ct));
    }

    [Fact]
    public async Task Summaries_of_two_workspaces_are_never_linked_by_the_service_or_by_the_schema()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, here) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var other = await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");
        var sent = await h.Conversations.StartAsync(other, "相机是怎么发明的？", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        var there = await h.SummarizeAsync(sent.Conversation);

        Assert.Equal(SummaryLinkRefusal.OtherWorkspace, (await Assert.ThrowsAsync<SummaryLinkRefusedException>(() => h.Links.CreateAsync(here, there, null, Ct))).Reason);
        Assert.DoesNotContain(await h.Links.CandidatesAsync(here, Ct), c => c.Node == there);

        // The schema refuses it too: both nodes are referenced together with the link's workspace.
        var (a, b) = SummaryLinkPair.Normalize(here, there);
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => h.Database.WriteAsync(
            (c, t) => c.Execute(t, "INSERT INTO summary_link (id, workspace_id, node_a, node_b, origin, created_at) VALUES ('x', $ws, $a, $b, 'user', 1);",
                ("$ws", h.Workspace.ToString()), ("$a", a.ToString()), ("$b", b.ToString())),
            Ct));
        Assert.Equal(0, await h.CountAsync("summary_link"));
    }

    [Fact]
    public async Task Deleting_a_link_deletes_only_it_and_records_the_pair_as_ignored_for_suggestions()
    {
        await using var h = await ChatHarness.StartAsync();
        var (rootA, a) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var (_, b) = await SummarizedRootAsync(h, "铁轨用什么做？");
        var link = await h.Links.CreateAsync(a, b, "对比", Ct);
        var before = await Facts(h, rootA, a);
        var changed = 0;
        h.Links.Changed += (_, _) => changed++;

        Assert.True(await h.Links.DeleteAsync(link.Id, Ct));

        Assert.Equal(1, changed);
        Assert.Equal(0, await h.CountAsync("summary_link"));
        Assert.Equal(before, await Facts(h, rootA, a));
        var ignored = await h.Database.ReadAsync(c => (c.Scalar<string>(null, "SELECT node_a || '|' || node_b || '|' || state FROM link_suggestion;"), c.Scalar<long>(null, "SELECT count(*) FROM link_suggestion;")), Ct);
        Assert.Equal(($"{link.NodeA}|{link.NodeB}|ignored", 1L), ignored);
        Assert.Empty(h.Database.ListBackups());

        // Linked again by the user and deleted again: still one ignored row for the pair.
        var again = await h.Links.CreateAsync(b, a, null, Ct);
        Assert.True(await h.Links.DeleteAsync(again.Id, Ct));
        Assert.Equal(1, await h.CountAsync("link_suggestion"));
    }

    [Fact]
    public async Task Deleting_a_summarys_last_version_removes_its_links_and_suggestions_and_leaves_the_others()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, a) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var (_, b) = await SummarizedRootAsync(h, "铁轨用什么做？");
        var (_, c) = await SummarizedRootAsync(h, "高铁为什么快？");
        await h.Links.CreateAsync(a, b, null, Ct);
        await h.Links.CreateAsync(a, c, null, Ct);
        var bc = await h.Links.CreateAsync(b, c, null, Ct);
        await SuggestAsync(h, a, b);
        Assert.Equal(2, await h.Links.CountTouchingAsync([a], Ct));
        Assert.Equal(3, await h.Links.CountTouchingAsync([a, b], Ct));

        var detail = (await h.SummaryService.GetAsync(a, Ct))!;
        Assert.Null(await h.SummaryService.DeleteVersionsAsync(a, detail.Versions.Select(v => v.Id).ToHashSet(), Ct));

        Assert.Equal([bc.Id], (await h.Links.ListAsync(h.Workspace, Ct)).Select(l => l.Id));
        Assert.Equal(0, await h.CountAsync("link_suggestion"));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));

        // Summarized again it is a new node; the old links do not come back.
        var conversation = detail.Node.Conversation;
        var renewed = await h.SummarizeAsync(conversation);
        Assert.NotEqual(a, renewed);
        Assert.Empty(await h.Links.ListForNodeAsync(renewed, Ct));
    }

    [Fact]
    public async Task Deleting_conversations_counts_the_links_that_go_and_removes_them_in_the_same_transaction()
    {
        await using var h = await ChatHarness.StartAsync();
        var (root, answer) = await h.RootWithAnswerAsync("火车是怎么发明的？");
        await h.SettleAsync(root);
        var rootNode = await SummarizeAsNamedAsync(h, root, "火车起源");
        var branch = await h.BranchAsync(root, answer);
        await h.AskAsync(branch, "谁造了第一台？");
        await h.SettleAsync(branch);
        var branchNode = await SummarizeAsNamedAsync(h, branch, "第一台机车");
        var (other, otherNode) = await SummarizedRootAsync(h, "铁轨用什么做？");
        var (third, thirdNode) = await SummarizedRootAsync(h, "高铁为什么快？");

        // root–branch is inside the scope, branch–other and root–third leave it; other–third stays.
        await h.Links.CreateAsync(rootNode, branchNode, null, Ct);
        await h.Links.CreateAsync(branchNode, otherNode, "例子", Ct);
        await h.Links.CreateAsync(rootNode, thirdNode, null, Ct);
        var kept = await h.Links.CreateAsync(otherNode, thirdNode, null, Ct);

        var scope = await h.ConversationDeletion.InspectAsync([root], Ct);
        Assert.Equal((2, 2, 3), (scope.Conversations.Count, scope.Summaries.Count, scope.Links));
        Assert.Equal(2, (await h.ConversationDeletion.InspectAsync([branch], Ct)).Links);
        Assert.Equal(0, (await h.ConversationDeletion.InspectAsync([await SummaryLessAsync(h)], Ct)).Links);

        // Multi-select: the root's subtree and the other root, links counted once each.
        var both = await h.ConversationDeletion.InspectAsync([root, other], Ct);
        Assert.Equal(4, both.Links);

        var outcome = await h.ConversationDeletion.DeleteAsync(scope, Ct);
        Assert.True(outcome.IsDeleted);
        Assert.Equal([kept.Id], (await h.Links.ListAsync(h.Workspace, Ct)).Select(l => l.Id));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));

        // The backup made first still holds the links that went.
        using var copy = SqliteConnections.OpenReadOnly(outcome.BackupPath!);
        Assert.Equal(4, copy.Scalar<long>(null, "SELECT count(*) FROM summary_link;"));

        // A multi-select delete of the rest takes the last link along.
        Assert.True((await h.DeleteAsync(other, third)).IsDeleted);
        Assert.Equal(0, await h.CountAsync("summary_link"));
    }

    [Fact]
    public async Task Deleting_a_workspace_counts_and_removes_its_links_and_forgets_its_hidden_links()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, a) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var (_, b) = await SummarizedRootAsync(h, "铁轨用什么做？");
        await h.Links.CreateAsync(a, b, "原因", Ct);
        await SuggestAsync(h, a, b, state: "ignored");
        await h.Links.SetHiddenAsync(h.Workspace, hidden: true, Ct);
        Assert.Equal("1", await h.Preferences.GetAsync(SummaryLinkService.HiddenKeyPrefix + h.Workspace, Ct));

        var other = await WorkspaceSeed.AddWorkspaceAsync(h, "摄影史");
        var otherA = await SummarizedAsync(h, other, "相机是怎么发明的？");
        var otherB = await SummarizedAsync(h, other, "胶片是什么？");
        var otherLink = await h.Links.CreateAsync(otherA, otherB, null, Ct);

        var contents = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);
        Assert.Equal((2, 1), (contents.Summaries, contents.Links));
        Assert.Equal(Application.Workspaces.WorkspaceDeleteStatus.Deleted, (await h.WorkspaceDeletion.DeleteAsync(h.Workspace, Ct)).Status);

        Assert.Equal([otherLink.Id.ToString()], await h.Database.ReadAsync(ListIds, Ct));
        Assert.Equal(0, await h.CountAsync("link_suggestion"));
        Assert.Null(await h.Preferences.GetAsync(SummaryLinkService.HiddenKeyPrefix + h.Workspace, Ct));
        Assert.False(await h.Links.AreHiddenAsync(h.Workspace, Ct));
        Assert.Null(await h.Database.ReadAsync(c => SqliteConnections.ForeignKeyViolation(c), Ct));
    }

    [Fact]
    public async Task Archiving_keeps_links_and_links_leave_context_counts_bodies_reads_and_the_export_alone()
    {
        await using var h = await ChatHarness.StartAsync();
        var (rootA, a) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var (rootB, b) = await SummarizedRootAsync(h, "铁轨用什么做？");
        var beforeA = await Facts(h, rootA, a);
        var beforeB = await Facts(h, rootB, b);
        var edges = (await h.SummaryNetwork.ProjectAsync(h.Workspace, Ct)).Edges.Count;
        var bases = await h.RecommendationStore.ListBasesAsync(h.Workspace, 12, 80, Ct);
        var workspace = await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct);

        var link = await h.Links.CreateAsync(a, b, "对比专用说明", Ct);

        // Nothing about either summary, its conversation, the network's sources or any count changed.
        Assert.Equal(beforeA, await Facts(h, rootA, a));
        Assert.Equal(beforeB, await Facts(h, rootB, b));
        Assert.Equal(edges, (await h.SummaryNetwork.ProjectAsync(h.Workspace, Ct)).Edges.Count);
        Assert.Equal(bases.Count, (await h.RecommendationStore.ListBasesAsync(h.Workspace, 12, 80, Ct)).Count);
        Assert.Equal(workspace.Summaries, (await h.WorkspaceDeletion.InspectAsync(h.Workspace, Ct)).Summaries);
        Assert.Equal(2, (await h.SummaryService.ListAsync(h.Workspace, Ct)).Count);

        // Continuing the conversation: its request carries nothing of the link or the other summary.
        h.Client.Requests.Clear();
        await h.AskAsync(rootA, "那它跑多快？");
        var request = Assert.Single(h.Client.Requests, FakeChatClient.IsAnswer);
        var sent = string.Join("\n", request.Turns.Select(t => t.Content));
        Assert.DoesNotContain("对比专用说明", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("铁轨用什么做", sent, StringComparison.Ordinal);

        // Archived: the summary stays in the network, and so does its link.
        Assert.True((await h.Conversations.ArchiveAsync(rootB, Ct)).Archived);
        Assert.Equal([link.Id], (await h.Links.ListAsync(h.Workspace, Ct)).Select(l => l.Id));

        // The Markdown export has the title and body only: no note, no other summary.
        var dir = Path.Combine(Path.GetTempPath(), "mindweave-link-export", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "总结.md");
            var export = new SummaryExportService(h.SummaryNodes, h.Store, new AtomicFileWriter(NullLogger<AtomicFileWriter>.Instance), NullLogger<SummaryExportService>.Instance);
            var current = (await h.SummaryService.GetAsync(a, Ct))!.Node.Current;
            await export.ExportAsync(a, current, null, new ExportDestination("总结.md", target, null), Ct);
            var text = await File.ReadAllTextAsync(target, Ct);
            Assert.DoesNotContain("对比专用说明", text, StringComparison.Ordinal);
            Assert.DoesNotContain("关联", text, StringComparison.Ordinal);
            Assert.DoesNotContain((await h.SummaryService.GetAsync(b, Ct))!.Node.DisplayName, text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task While_storage_is_paused_no_link_is_made_changed_or_deleted_and_a_failed_write_pauses_it()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, a) = await SummarizedRootAsync(h, "火车是怎么发明的？");
        var (_, b) = await SummarizedRootAsync(h, "铁轨用什么做？");
        var link = await h.Links.CreateAsync(a, b, "对比", Ct);
        h.Health.Report(new StorageWriteException(StorageFailureKind.Full, "disk full", null));

        await Assert.ThrowsAsync<StorageWriteException>(() => h.Links.CreateAsync(b, a, null, Ct));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.Links.EditNoteAsync(link.Id, "原因", Ct));
        await Assert.ThrowsAsync<StorageWriteException>(() => h.Links.DeleteAsync(link.Id, Ct));
        Assert.Equal("对比", Assert.Single(await h.Links.ListAsync(h.Workspace, Ct)).Note);
    }

    private static List<string> ListIds(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        using var command = c.Command(null, "SELECT id FROM summary_link ORDER BY created_at;");
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    /// <summary>A root conversation with one answer, summarized; its title settled first so later counts do not race it.</summary>
    internal static async Task<(ConversationId Conversation, SummaryNodeId Node)> SummarizedRootAsync(ChatHarness h, string question)
    {
        var (root, _) = await h.RootWithAnswerAsync(question);
        await h.SettleAsync(root);
        return (root, await SummarizeAsNamedAsync(h, root, question));
    }

    private static async Task<SummaryNodeId> SummarizedAsync(ChatHarness h, Core.Workspaces.WorkspaceId workspace, string question)
    {
        var sent = await h.Conversations.StartAsync(workspace, question, Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);
        return await SummarizeAsNamedAsync(h, sent.Conversation, question);
    }

    /// <summary>
    /// Summarizes with a title of its own, so no two summaries share a name and no background naming task renames one
    /// while a test compares names.
    /// </summary>
    private static Task<SummaryNodeId> SummarizeAsNamedAsync(ChatHarness h, ConversationId conversation, string question)
    {
        var name = question.TrimEnd('？');
        h.Client.Knowledge.Replies.Enqueue($"标题：{name}的总结\n短名：{name}\n\n关于{name}的正文。");
        return h.SummarizeAsync(conversation);
    }

    private static async Task<ConversationId> SummaryLessAsync(ChatHarness h)
    {
        var (root, _) = await h.RootWithAnswerAsync("没有总结的会话");
        await h.SettleAsync(root);
        return root;
    }

    /// <summary>A link suggestion row as 8b will store it (no service yet): pending from a request, or ignored.</summary>
    private static Task<int> SuggestAsync(ChatHarness h, SummaryNodeId first, SummaryNodeId second, string state = "pending")
    {
        var (a, b) = SummaryLinkPair.Normalize(first, second);
        return h.Database.WriteAsync(
            (c, t) => c.Execute(t, """
                INSERT INTO link_suggestion (id, workspace_id, node_a, node_b, note, reason, state, generation_id, created_at, updated_at)
                VALUES ($id, $ws, $a, $b, '对比', CASE WHEN $state = 'pending' THEN '理由' END, $state, CASE WHEN $state = 'pending' THEN 'g' END, 1, 1);
                """, ("$id", Guid.NewGuid().ToString("D")), ("$ws", h.Workspace.ToString()), ("$a", a.ToString()), ("$b", b.ToString()), ("$state", state)),
            Ct);
    }

    /// <summary>What a link must never change about a summary and its conversation: body, versions, read state, messages.</summary>
    private static async Task<string> Facts(ChatHarness h, ConversationId conversation, SummaryNodeId node)
    {
        var detail = (await h.SummaryService.GetAsync(node, Ct))!;
        var messages = await h.Store.GetMessagesAsync(conversation, Ct);
        return string.Join("|", detail.Versions.Select(v => $"{v.Id}:{v.Title}:{v.Body}:{detail.ReadState(v.Id).IsRead}"))
            + $"|{detail.Node.DisplayName}|{detail.Node.Current}|{detail.Markers}|{messages.Count}|{string.Join(",", messages.Select(m => m.Text))}";
    }
}
