using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading version-18 data for import (docs/design/sharing-import.md §6): every branch keeps its source, its frozen ranges
/// and its place in the order of its parent's branches. A branch may have no source message, or a source message without a
/// quoted sentence; a range may say that nothing of an ancestor is used; imports and what came through them start empty.
/// A failing later step leaves version 18 as it was.
/// </summary>
public sealed class SchemaV19MigrationTests : IDisposable
{
    private const int Version = 19;
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Root = "0190f000-0000-7000-8000-000000000101";
    private const string Late = "0190f000-0000-7000-8000-000000000102";
    private const string Early = "0190f000-0000-7000-8000-000000000103";
    private const string Deep = "0190f000-0000-7000-8000-000000000104";
    private const string Empty = "0190f000-0000-7000-8000-000000000105";
    private const string Question = "0190f000-0000-7000-8000-000000000201";
    private const string Answer = "0190f000-0000-7000-8000-000000000202";
    private const string DeepAnswer = "0190f000-0000-7000-8000-000000000204";

    private const string Sources = """
        SELECT group_concat(line, char(10)) FROM (
            SELECT rowid || '|' || child_id || '|' || parent_id || '|' || source_message_id || '|' || source_block || '|' || quote || '|' || anchor || '|'
                || anchor_version || '|' || render_profile || '|' || created_at AS line
            FROM branch_source ORDER BY rowid);
        """;

    private const string Ranges = "SELECT group_concat(child_id || '|' || ancestor_id || '|' || depth || '|' || max_seq, char(10)) FROM (SELECT * FROM branch_context_range ORDER BY child_id, depth);";

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_18_branches_are_kept_row_for_row()
    {
        await SeedPreviousAsync();
        string? sources;
        string? ranges;
        using (var before = _dir.Raw())
        {
            sources = Scalar<string>(before, Sources);
            ranges = Scalar<string>(before, Ranges);
        }

        Assert.Contains(Late, sources, StringComparison.Ordinal);
        await UpgradeAsync();

        using var raw = _dir.Raw();
        Assert.Equal(Version, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(sources, Scalar<string>(raw, Sources));
        Assert.Equal(ranges, Scalar<string>(raw, Ranges));
        Assert.Equal(3, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name IN ('branch_source_by_parent', 'branch_source_by_message', 'branch_context_range_by_ancestor');"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT (SELECT count(*) FROM import) + (SELECT count(*) FROM import_conversation);"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name LIKE '%v19%';"));
    }

    [Fact]
    public async Task The_store_reads_a_branchs_source_and_its_parents_branches_as_before()
    {
        await SeedPreviousAsync();
        await using var database = _dir.Database();
        Assert.True((await database.OpenAsync(Ct)).IsReady);
        var branches = new SqliteBranchStore(database);

        var source = await branches.GetSourceAsync(ConversationId.Parse(Late), Ct);
        Assert.NotNull(source);
        Assert.Equal(MessageId.Parse(Answer), source.SourceMessage);
        Assert.Equal("the quoted\r\nsentence", source.Quote);
        Assert.Equal((0, "{\"a\":1}", 2, "livemarkdown-1/x"), (source.SourceBlock, source.AnchorJson, source.AnchorFormatVersion, source.RenderProfile));

        // Made at the same moment: the order they were stored in decides, as before.
        Assert.Equal([ConversationId.Parse(Late), ConversationId.Parse(Early)], (await branches.ListChildrenAsync(ConversationId.Parse(Root), Ct)).Select(s => s.Child));
        var context = await branches.GetAncestorContextAsync(ConversationId.Parse(Deep), Ct);
        Assert.Equal([(ConversationId.Parse(Root), 2), (ConversationId.Parse(Late), 2)], context.Select(s => (s.Conversation, s.Messages.Count)));
    }

    [Fact]
    public async Task A_branch_may_have_no_source_or_no_sentence_and_may_use_nothing_of_an_ancestor()
    {
        await SeedPreviousAsync();
        await UpgradeAsync();
        using var raw = _dir.Raw();
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");

        void Insert(string values) => SqliteConnections.Execute(raw, $"""
            INSERT INTO branch_source (child_id, parent_id, source_message_id, source_block, source_offset, quote, anchor, anchor_version, render_profile, created_at)
            VALUES ('{Empty}', '{Root}', {values}, 9);
            """);
        void Allowed(string values)
        {
            Insert(values);
            SqliteConnections.Execute(raw, $"DELETE FROM branch_source WHERE child_id = '{Empty}';");
        }

        void Refused(string values) => Assert.Throws<SqliteException>(() => Insert(values));

        Allowed("NULL, NULL, NULL, NULL, NULL, NULL, NULL");
        Allowed($"'{Answer}', NULL, NULL, NULL, NULL, NULL, NULL");
        Allowed($"'{Answer}', NULL, NULL, 'a sentence', NULL, NULL, NULL");
        Allowed($"'{Answer}', 0, 12, 'a sentence', NULL, NULL, NULL");
        Allowed($"'{Answer}', 0, NULL, 'a sentence', 'x', 1, 'p'");
        Refused("NULL, NULL, NULL, 'a sentence', NULL, NULL, NULL"); // a sentence of no answer
        Refused($"'{Answer}', 0, NULL, NULL, NULL, NULL, NULL"); // a place of no sentence
        Refused($"'{Answer}', NULL, 12, 'a sentence', NULL, NULL, NULL"); // an offset in no block
        Refused($"'{Answer}', 0, NULL, NULL, 'x', 1, 'p'"); // an anchor of no sentence
        Refused($"'{Answer}', 0, NULL, 'a sentence', 'x', NULL, 'p'"); // an anchor without its version
        Refused($"'{Answer}', 0, NULL, 'a sentence', NULL, 1, 'p'"); // a version without its anchor
        Refused($"'{Answer}', 0, NULL, '', NULL, NULL, NULL");
        Refused("'0190f000-0000-7000-8000-000000000999', NULL, NULL, NULL, NULL, NULL, NULL");

        SqliteConnections.Execute(raw, $"INSERT INTO branch_context_range VALUES ('{Empty}', '{Root}', 0, -1);");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"UPDATE branch_context_range SET max_seq = -2 WHERE child_id = '{Empty}';"));
    }

    [Fact]
    public async Task A_received_conversation_names_its_import_and_an_import_goes_with_its_last_one()
    {
        await SeedPreviousAsync();
        await UpgradeAsync();
        using var raw = _dir.Raw();
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");
        SqliteConnections.Execute(raw, $"""
            INSERT INTO import VALUES ('i1', 'Trains', 'structure', 5);
            INSERT INTO import_conversation VALUES ('{Empty}', 'i1');
            INSERT INTO import_conversation VALUES ('{Deep}', 'i1');
            """);
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO import VALUES ('i2', 'Trains', 'other', 5);"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO import_conversation VALUES ('{Early}', 'nope');"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "DELETE FROM import;"));

        SqliteConnections.Execute(raw, $"DELETE FROM conversation WHERE id = '{Empty}';");
        Assert.Equal((1L, 1L), (Scalar<long>(raw, "SELECT count(*) FROM import;"), Scalar<long>(raw, "SELECT count(*) FROM import_conversation;")));
        SqliteConnections.Execute(raw, $"DELETE FROM branch_source WHERE child_id = '{Deep}'; DELETE FROM message WHERE conversation_id = '{Deep}'; DELETE FROM conversation WHERE id = '{Deep}';");
        Assert.Equal((0L, 0L), (Scalar<long>(raw, "SELECT count(*) FROM import;"), Scalar<long>(raw, "SELECT count(*) FROM import_conversation;")));
    }

    [Fact]
    public async Task A_failing_upgrade_after_this_step_leaves_version_18_as_it_was()
    {
        await SeedPreviousAsync();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version), new Migration(Version + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(Version - 1, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(3, Scalar<long>(raw, "SELECT count(*) FROM branch_source;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name IN ('import', 'import_conversation');"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('branch_source') WHERE name = 'source_offset';"));
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    private async Task UpgradeAsync()
    {
        await using var database = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version)));
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(Version - 1, Assert.Single(database.ListBackups()).SchemaVersion);
        await database.CloseAsync();
    }

    /// <summary>A conversation with two branches made at the same moment, stored out of identity order, one with a branch of its own; and a conversation without messages.</summary>
    private async Task SeedPreviousAsync()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version < Version))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, $$"""
            BEGIN;
            INSERT INTO workspace VALUES ('{{Workspace}}', '火车', 1);
            INSERT INTO conversation VALUES ('{{Root}}', '{{Workspace}}', 'Trains', 'manual', 1, 1);
            INSERT INTO conversation VALUES ('{{Late}}', '{{Workspace}}', NULL, 'none', 2, 2);
            INSERT INTO conversation VALUES ('{{Early}}', '{{Workspace}}', NULL, 'none', 2, 2);
            INSERT INTO conversation VALUES ('{{Deep}}', '{{Workspace}}', NULL, 'none', 3, 3);
            INSERT INTO conversation VALUES ('{{Empty}}', '{{Workspace}}', NULL, 'none', 4, 4);
            INSERT INTO message (id, conversation_id, seq, role, state, created_at) VALUES
                ('{{Question}}', '{{Root}}', 0, 'user', 'complete', 1), ('{{Answer}}', '{{Root}}', 1, 'assistant', 'complete', 1),
                ('0190f000-0000-7000-8000-000000000203', '{{Late}}', 0, 'user', 'complete', 1), ('{{DeepAnswer}}', '{{Late}}', 1, 'assistant', 'complete', 1);
            INSERT INTO message_block (message_id, ordinal, kind, text) VALUES
                ('{{Question}}', 0, 'text', 'a question'), ('{{Answer}}', 0, 'text', 'an answer'),
                ('0190f000-0000-7000-8000-000000000203', 0, 'text', 'one more'), ('{{DeepAnswer}}', 0, 'text', 'another');
            INSERT INTO branch_source VALUES ('{{Late}}', '{{Root}}', '{{Answer}}', 0, 'the quoted' || char(13) || char(10) || 'sentence', '{"a":1}', 2, 'livemarkdown-1/x', 2);
            INSERT INTO branch_source VALUES ('{{Early}}', '{{Root}}', '{{Answer}}', 0, '另一句', '{}', 1, 'livemarkdown-1/x', 2);
            INSERT INTO branch_source VALUES ('{{Deep}}', '{{Late}}', '{{DeepAnswer}}', 0, 'another', '{}', 1, 'livemarkdown-1/x', 3);
            INSERT INTO branch_context_range VALUES ('{{Late}}', '{{Root}}', 0, 1), ('{{Early}}', '{{Root}}', 0, 1), ('{{Deep}}', '{{Root}}', 0, 1), ('{{Deep}}', '{{Late}}', 1, 1);
            COMMIT;
            """);
    }
}
