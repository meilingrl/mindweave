using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Branches;
using Mindweave.Core.Branches;
using Mindweave.Core.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class BranchStorageTests
{
    internal const string Profile = "livemarkdown/2.5.1;mindweave/1";
    internal static AiConfiguration Config(int budget = 32768) => new(Guid.NewGuid(), Guid.NewGuid(), "test", "http://127.0.0.1:1/v1", "synthetic", "test", budget, 128);
    internal static SourceSelection Selection(ChatMessage message) => new(message.Id, message.Revision,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Body))), message.Body, new(1, Profile, "{}"));
    internal static async Task<ChatMessage> Answer(SqliteWorkspaceStore store, Guid conversation, string? question, string body, GenerationStatus status = GenerationStatus.Completed)
    {
        var work = await store.AcceptReplyAsync(conversation, question, Config());
        await store.WriteGenerationAsync(work.Task.Id, 1, body, status);
        return (await store.ReadMessagesAsync(conversation)).Last();
    }

    [Fact]
    public async Task Branch_source_frozen_ids_and_parent_read_position_commit_once_and_reopen()
    {
        using var dir = new TestDirectory();
        var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        Guid parentId, childId, sourceId;
        ConversationReadState read;
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths))
        {
            IBranchStore branches = store;
            var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
            parentId = parent.Id;
            var answer = await Answer(store, parentId, "question", "selected answer"); sourceId = answer.Id;
            read = new(parentId, new(sourceId, 0, 12.5, 1, Profile), 2);
            var operation = Guid.NewGuid(); var selection = Selection(answer);
            var child = await branches.CreateBranchAsync(parentId, selection, operation, read); childId = child.Id;
            Assert.Equal(child, await branches.CreateBranchAsync(parentId, selection, operation, read));
            Assert.Equal(parent.WorkspaceId, child.WorkspaceId);
            var source = Assert.Single(await branches.ListChildBranchesAsync(parentId));
            Assert.Equal(new BranchSource(childId, parentId, sourceId, selection.Quote, selection.Anchor), source);
            Assert.Equal(source, await branches.LoadBranchSourceAsync(childId));
            Assert.Null(await branches.LoadBranchSourceAsync(parentId));
            Assert.Equal(new[] { (await store.ReadMessagesAsync(parentId))[0].Id, sourceId }, Assert.Single(await branches.ReadFrozenContextRangesAsync(childId)).MessageIds);
            await Assert.ThrowsAsync<InvalidOperationException>(() => branches.CreateBranchAsync(parentId, selection with { Quote = "changed" }, operation));
            await Assert.ThrowsAsync<InvalidOperationException>(() => branches.CreateBranchAsync(childId, selection, operation));
            Assert.Equal(2, (await store.ListConversationsAsync(parent.WorkspaceId)).Count);
        }
        await using var reopened = await SqliteWorkspaceStore.OpenAsync(paths);
        Assert.Equal(read, await reopened.LoadReadStateAsync(parentId));
        Assert.Equal(sourceId, (await reopened.LoadBranchSourceAsync(childId))!.MessageId);
        Assert.Equal(new[] { "question", "selected answer" }, (await reopened.ReadContextAsync(childId)).Messages.Select(m => m.Body));
    }

    [Theory]
    [InlineData("user")][InlineData("queued")][InlineData("running")][InlineData("empty")]
    [InlineData("revision")][InlineData("hash")][InlineData("workspace")][InlineData("sibling")]
    [InlineData("quote")][InlineData("version")][InlineData("profile")][InlineData("payload")][InlineData("array")][InlineData("oversize")][InlineData("operation")]
    public async Task Invalid_source_or_envelope_never_creates_partial_branch(string invalid)
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var workspace = await store.CreateAsync("test");
        var parent = await store.CreateConversationAsync(workspace.Id);
        var work = await store.AcceptReplyAsync(parent.Id, "q", Config());
        if (invalid != "queued") await store.WriteGenerationAsync(work.Task.Id, 1, invalid == "empty" ? "" : "answer", invalid == "running" ? GenerationStatus.Running : GenerationStatus.Completed);
        var messages = await store.ReadMessagesAsync(parent.Id);
        var selection = Selection(invalid == "user" ? messages[0] : messages[^1]);
        var target = parent.Id;
        if (invalid is "workspace" or "sibling") target = (await store.CreateConversationAsync(invalid == "workspace" ? (await store.CreateAsync("other")).Id : workspace.Id)).Id;
        selection = invalid switch
        {
            "revision" => selection with { MessageRevision = 99 }, "hash" => selection with { SourceBodyHash = new('A', 64) },
            "quote" => selection with { Quote = " " }, "version" => selection with { Anchor = selection.Anchor with { FormatVersion = 99 } },
            "profile" => selection with { Anchor = selection.Anchor with { RendererProfile = "unknown" } },
            "payload" => selection with { Anchor = selection.Anchor with { Payload = "not json" } },
            "array" => selection with { Anchor = selection.Anchor with { Payload = "[]" } },
            "oversize" => selection with { Anchor = selection.Anchor with { Payload = "{\"x\":\"" + new string('x', 1_048_576) + "\"}" } }, _ => selection
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.CreateBranchAsync(target, selection, invalid == "operation" ? Guid.Empty : Guid.NewGuid()));
        Assert.Empty(await store.ListChildBranchesAsync(target));
    }

    [Theory]
    [InlineData(GenerationStatus.Completed)][InlineData(GenerationStatus.Stopped)][InlineData(GenerationStatus.Failed)][InlineData(GenerationStatus.Interrupted)]
    public async Task Saved_terminal_old_attempt_can_be_frozen_after_a_retry(GenerationStatus status)
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var old = await Answer(store, parent.Id, "q", "old", status);
        await Answer(store, parent.Id, null, "new");
        var child = await store.CreateBranchAsync(parent.Id, Selection(old), Guid.NewGuid());
        Assert.Equal(new[] { "q", "old" }, (await store.ReadContextAsync(child.Id)).Messages.Select(m => m.Body));
    }

    [Fact]
    public async Task Nested_branches_keep_exact_ancestors_while_own_history_uses_latest_attempt_and_titles_use_own_pairs()
    {
        using var dir = new TestDirectory();
        await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var a = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var a1 = await Answer(store, a.Id, "Aq", "Aold");
        var b = await store.CreateBranchAsync(a.Id, Selection(a1), Guid.NewGuid());
        Assert.Null(await store.AcceptTitleAsync(b.Id, Config()));
        var b1 = await Answer(store, b.Id, "Bq", "Bold");
        var c = await store.CreateBranchAsync(b.Id, Selection(b1), Guid.NewGuid());
        await Answer(store, a.Id, null, "Anew"); await Answer(store, a.Id, "Alaterq", "Alater");
        await Answer(store, b.Id, null, "Bnew"); await Answer(store, b.Id, "Blaterq", "Blater");
        var sibling = await store.CreateBranchAsync(a.Id, Selection(a1), Guid.NewGuid());
        await Answer(store, sibling.Id, "siblingq", "sibling");
        var ranges = await store.ReadFrozenContextRangesAsync(c.Id);
        Assert.Equal(new[] { a.Id, b.Id }, ranges.Select(r => r.ConversationId));
        Assert.Equal(new[] { "Aq", "Aold", "Bq", "Bold" }, (await store.ReadContextAsync(c.Id)).Messages.Select(m => m.Body));
        var c1 = await Answer(store, c.Id, "Cq", "Cold");
        var retry = await store.AcceptReplyAsync(c.Id, null, Config());
        Assert.Equal(new[] { "Aq", "Aold", "Bq", "Bold", "Cq" }, retry.InputMessages.Select(m => m.Body));
        Assert.Equal("Bold", retry.SourceQuote);
        Assert.DoesNotContain(c1.Id, retry.Task.InputMessageIds);
        Assert.Contains(a1.Id, retry.Task.InputMessageIds); Assert.Contains(b1.Id, retry.Task.InputMessageIds);
        await store.WriteGenerationAsync(retry.Task.Id, 1, "Cnew", GenerationStatus.Completed);
        Assert.Equal(new[] { "Aq", "Aold", "Bq", "Bold", "Cq", "Cnew" }, (await store.ReadContextAsync(c.Id)).Messages.Select(m => m.Body));
        var title = Assert.IsType<GenerationWork>(await store.AcceptTitleAsync(c.Id, Config()));
        Assert.Equal(new[] { "Cq", "Cnew" }, title.InputMessages.Select(m => m.Body)); Assert.Null(title.SourceQuote);
    }

    [Theory]
    [InlineData("ABORT")]
    [InlineData("ROLLBACK")]
    public async Task Late_branch_failure_rolls_back_child_source_ranges_and_parent_read_state(string failure)
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var source = await Answer(store, parent.Id, "q", "a");
        var old = new ConversationReadState(parent.Id, null, 1); await store.SaveReadStateAsync(old);
        Sql(paths, $"CREATE TRIGGER fail_read BEFORE UPDATE ON conversation_read_state BEGIN SELECT RAISE({failure},'injected'); END");
        var op = Guid.NewGuid();
        await Assert.ThrowsAsync<SqliteException>(() => store.CreateBranchAsync(parent.Id, Selection(source), op, new(parent.Id, new(source.Id, 0, 2, 1, Profile), 2)));
        Assert.Single(await store.ListConversationsAsync(parent.WorkspaceId)); Assert.Empty(await store.ListChildBranchesAsync(parent.Id)); Assert.Equal(old, await store.LoadReadStateAsync(parent.Id));
        Sql(paths, "DROP TRIGGER fail_read");
        var child = await store.CreateBranchAsync(parent.Id, Selection(source), op);
        Assert.Single(await store.ReadFrozenContextRangesAsync(child.Id));
    }

    [Theory]
    [InlineData("revision")][InlineData("owner")][InlineData("version")][InlineData("profile")][InlineData("start")][InlineData("nan")][InlineData("infinity")][InlineData("negative")]
    public async Task Invalid_read_state_is_rejected(string invalid)
    {
        using var dir = new TestDirectory(); await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var source = await Answer(store, parent.Id, "q", "a");
        var location = new ReadingLocation(source.Id, 0, 0, 1, Profile);
        location = invalid switch { "owner" => location with { MessageId = Guid.NewGuid() }, "version" => location with { FormatVersion = 2 }, "profile" => location with { RendererProfile = "other" }, "start" => location with { SourceStart = -1 }, "nan" => location with { OffsetWithinBlock = double.NaN }, "infinity" => location with { OffsetWithinBlock = double.PositiveInfinity }, "negative" => location with { OffsetWithinBlock = -1 }, _ => location };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SaveReadStateAsync(new(parent.Id, location, invalid == "revision" ? 0 : 1)));
        Assert.Null(await store.LoadReadStateAsync(parent.Id));
    }

    [Fact]
    public async Task Read_state_revision_is_independent_and_old_writes_cannot_overwrite_beginning()
    {
        using var dir = new TestDirectory(); await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var source = await Answer(store, parent.Id, "q", "a");
        var old = new ConversationReadState(parent.Id, new(source.Id, 0, 2, 1, Profile), 1);
        await store.SaveReadStateAsync(old); await store.SaveReadStateAsync(new(parent.Id, null, 3)); await store.SaveReadStateAsync(old with { Revision = 2 });
        Assert.Equal(new(parent.Id, null, 3), await store.LoadReadStateAsync(parent.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateBranchAsync(parent.Id, Selection(source), Guid.NewGuid(), old with { ConversationId = Guid.NewGuid() }));
        Assert.Single(await store.ListConversationsAsync(parent.WorkspaceId));
    }

    [Fact]
    public async Task Transaction_budget_counts_frozen_quote_and_rolls_back_question_and_draft()
    {
        using var dir = new TestDirectory(); await using var store = await SqliteWorkspaceStore.OpenAsync(AppPaths.ForTrack("codex-steady", dir.Path));
        var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var source = await Answer(store, parent.Id, "q", new string('x', 300));
        var child = await store.CreateBranchAsync(parent.Id, Selection(source), Guid.NewGuid());
        await store.SaveDraftAsync(child.Id, "preserved", 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcceptReplyAsync(child.Id, "preserved", Config(1000)));
        Assert.Empty(await store.ReadMessagesAsync(child.Id)); Assert.Equal(new("preserved", 1), await store.LoadDraftAsync(child.Id)); Assert.Empty(await store.ListActiveGenerationsAsync());
    }

    [Fact]
    public async Task Upgrade_v3_backs_up_before_installing_branch_schema()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path); Guid parent;
        await using (var old = await SqliteWorkspaceStore.OpenAsync(paths, DatabaseSchema.Migrations.Take(3).ToArray()))
            parent = (await old.CreateConversationAsync((await old.CreateAsync("v3")).Id)).Id;
        await using var upgraded = await SqliteWorkspaceStore.OpenAsync(paths);
        var source = await Answer(upgraded, parent, "q", "a"); await upgraded.CreateBranchAsync(parent, Selection(source), Guid.NewGuid());
        var backup = Assert.Single(Directory.GetFiles(paths.BackupDirectory, "*.db"));
        using var connection = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version"; Assert.Equal(3L, command.ExecuteScalar());
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='branch_source'"; Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Failed_terminal_save_cannot_be_used_as_a_branch_source_until_persistence_succeeds()
    {
        using var dir = new TestDirectory(); var paths = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
        var parent = await store.CreateConversationAsync((await store.CreateAsync("test")).Id);
        var work = await store.AcceptReplyAsync(parent.Id, "q", Config());
        Sql(paths, "CREATE TRIGGER fail_terminal BEFORE UPDATE ON generation_task BEGIN SELECT RAISE(ABORT,'injected'); END");
        await Assert.ThrowsAsync<SqliteException>(() => store.WriteGenerationAsync(work.Task.Id, 1, "not yet saved", GenerationStatus.Completed));
        var dirty = (await store.ReadMessagesAsync(parent.Id)).Last() with { Body = "not yet saved", Revision = 1, Status = GenerationStatus.Completed };
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateBranchAsync(parent.Id, Selection(dirty), Guid.NewGuid()));
        Assert.Empty(await store.ListChildBranchesAsync(parent.Id));
        Sql(paths, "DROP TRIGGER fail_terminal");
        await store.WriteGenerationAsync(work.Task.Id, 1, dirty.Body, GenerationStatus.Completed);
        var operation = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.CreateBranchAsync(parent.Id, Selection(dirty), operation)));
        Assert.Single(results.Select(child => child.Id).Distinct());
        Assert.Single(await store.ListChildBranchesAsync(parent.Id));
    }

    internal static void Sql(AppPaths paths, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Foreign Keys=True;Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
