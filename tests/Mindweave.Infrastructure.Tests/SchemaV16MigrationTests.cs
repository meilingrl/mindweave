using Microsoft.Data.Sqlite;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrading version-15 data to the phase-1 resource schema (architecture §15.2, docs/design/attachments.md). A trusted
/// resource becomes provenance 'user' (an untrusted one 'external'), an image file becomes origin 'image', derivatives keep
/// their rows; chunks with their keyword index, draft attachments, a task's resource inputs and a request's purpose start
/// empty. The schema keeps the index in step with chunks, drops a draft row with its resource or conversation, and allows
/// one derivative per resource, kind, producer and prompt version. A failing later step leaves version 15 as it was.
/// </summary>
public sealed class SchemaV16MigrationTests : IDisposable
{
    private const int Version = 16;
    private const string Workspace = "0190f000-0000-7000-8000-0000000000aa";
    private const string Conversation = "0190f000-0000-7000-8000-000000000101";
    private static readonly string Hash = new('a', 64);
    private static readonly string ImageHash = new('b', 64);

    private readonly TempDataDirectory _dir = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Version_15_resources_are_kept_with_provenance_and_the_new_tables_hold_their_rules()
    {
        await SeedPreviousAsync();

        await using var database = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version)));
        var result = await database.OpenAsync(Ct);
        Assert.True(result.IsReady, $"{result.Status}: {result.ErrorDetail}");
        Assert.Equal(Version - 1, Assert.Single(database.ListBackups()).SchemaVersion);
        await database.CloseAsync();

        using var raw = _dir.Raw();
        Assert.Equal(Version, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('resource') WHERE name = 'trusted';"));
        Assert.Equal("doc:paste:user|ext:file:external|pic:image:user", Scalar<string>(raw, "SELECT group_concat(id || ':' || origin || ':' || provenance, '|') FROM (SELECT * FROM resource ORDER BY id);"));
        Assert.Equal(("全文", "plain-text/1", 0L), (Scalar<string>(raw, "SELECT text FROM resource_derivative;"), Scalar<string>(raw, "SELECT producer FROM resource_derivative;"), Scalar<long>(raw, "SELECT truncated FROM resource_derivative;")));
        Assert.Equal(("[]", 0L), (Scalar<string>(raw, "SELECT input_resources FROM generation_task;"), Scalar<long>(raw, "SELECT count(*) FROM ai_request WHERE purpose IS NOT NULL;")));
        Assert.Equal(0, Scalar<long>(raw, "SELECT (SELECT count(*) FROM resource_chunk) + (SELECT count(*) FROM resource_chunk_fts) + (SELECT count(*) FROM draft_attachment);"));
        SqliteConnections.Execute(raw, "PRAGMA foreign_keys = ON;");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "UPDATE resource SET provenance = 'trusted' WHERE id = 'doc';"));

        // One derivative per resource, kind, producer and prompt version: a second summary of the same prompt is refused, a
        // new prompt version or another model's is a row of its own.
        SqliteConnections.Execute(raw, "INSERT INTO resource_derivative (resource_id, kind, producer, prompt_version, text, created_at) VALUES ('doc', 'summary', 'm', '1', '摘要', 2);");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO resource_derivative (resource_id, kind, producer, prompt_version, text, created_at) VALUES ('doc', 'summary', 'm', '1', '又一份', 3);"));
        SqliteConnections.Execute(raw, "INSERT INTO resource_derivative (resource_id, kind, producer, prompt_version, text, created_at) VALUES ('doc', 'summary', 'm', '2', '新提示词', 3);");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO resource_derivative (resource_id, kind, producer, text, created_at) VALUES ('doc', 'text', 'plain-text/1', '重复的全文', 3);"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO resource_derivative (resource_id, kind, producer, text, blob_sha256, created_at) VALUES ('pic', 'image_scaled', 's', 'image/png', '{new string('C', 64)}', 3);"));

        // Chunks and their index go together, also when the resource goes.
        SqliteConnections.Execute(raw, """
            INSERT INTO resource_chunk (resource_id, ordinal, start_char, end_char, estimated_tokens) VALUES ('doc', 0, 0, 2, 2);
            INSERT INTO resource_chunk_fts (rowid, terms) VALUES (last_insert_rowid(), '全文');
            """);
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM resource_chunk_fts WHERE resource_chunk_fts MATCH '全文';"));
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, "INSERT INTO resource_chunk (resource_id, ordinal, start_char, end_char, estimated_tokens) VALUES ('doc', 1, 5, 5, 0);"));

        // Draft rows: one per resource; they go with the resource or with the conversation.
        SqliteConnections.Execute(raw, $"INSERT INTO draft_attachment (resource_id, workspace_id, conversation_id, slot_key, ordinal, created_at) VALUES ('ext', '{Workspace}', '{Conversation}', 'main', 0, 1);");
        SqliteConnections.Execute(raw, $"INSERT INTO draft_attachment (resource_id, workspace_id, conversation_id, slot_key, ordinal, created_at) VALUES ('pic', '{Workspace}', NULL, 'new-exploration', 0, 1);");
        Assert.Throws<SqliteException>(() => SqliteConnections.Execute(raw, $"INSERT INTO draft_attachment (resource_id, workspace_id, conversation_id, slot_key, ordinal, created_at) VALUES ('pic', '{Workspace}', NULL, 'other', 1, 1);"));
        SqliteConnections.Execute(raw, "DELETE FROM resource WHERE id = 'doc';");
        Assert.Equal(0, Scalar<long>(raw, "SELECT (SELECT count(*) FROM resource_chunk) + (SELECT count(*) FROM resource_chunk_fts) + (SELECT count(*) FROM resource_derivative WHERE resource_id = 'doc');"));
        SqliteConnections.Execute(raw, $"BEGIN; DELETE FROM generation_task; DELETE FROM message WHERE conversation_id = '{Conversation}'; DELETE FROM conversation WHERE id = '{Conversation}'; COMMIT;");
        Assert.Equal("pic", Scalar<string>(raw, "SELECT group_concat(resource_id) FROM draft_attachment;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task A_failing_upgrade_after_this_step_leaves_version_15_as_it_was()
    {
        await SeedPreviousAsync();

        await using (var failing = _dir.Database(new MigrationCatalog([.. MigrationCatalog.Default.Migrations.Where(m => m.Version <= Version), new Migration(Version + 1, "broken", (_, _) => throw new InvalidOperationException("simulated failure"))])))
        {
            Assert.Equal(DatabaseOpenStatus.MigrationFailed, (await failing.OpenAsync(Ct)).Status);
        }

        using var raw = _dir.Raw();
        Assert.Equal(Version - 1, Scalar<long>(raw, "PRAGMA user_version;"));
        Assert.Equal(0, Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE name IN ('resource_chunk', 'resource_chunk_fts', 'draft_attachment');"));
        Assert.Equal(1, Scalar<long>(raw, "SELECT count(*) FROM pragma_table_info('resource') WHERE name = 'trusted';"));
        Assert.Equal("file", Scalar<string>(raw, "SELECT origin FROM resource WHERE id = 'pic';"));
    }

    private static T? Scalar<T>(SqliteConnection connection, string sql) => SqliteConnections.Scalar<T>(connection, sql);

    /// <summary>A workspace with a conversation, an answer task and its request; a trusted paste with text, an untrusted file, an image file.</summary>
    private async Task SeedPreviousAsync()
    {
        await using (var old = _dir.Database(new MigrationCatalog(MigrationCatalog.Default.Migrations.Where(m => m.Version < Version))))
        {
            Assert.True((await old.OpenAsync(Ct)).IsReady);
        }

        using var seed = _dir.Raw();
        SqliteConnections.Execute(seed, $"""
            BEGIN;
            INSERT INTO workspace VALUES ('{Workspace}', '测试', 1);
            INSERT INTO conversation VALUES ('{Conversation}', '{Workspace}', NULL, 'none', 1, 1);
            INSERT INTO generation_task (id, kind, conversation_id, state, model_config_id, base_url, protocol, model, credential_ref, input_message_ids, created_at)
            VALUES ('t1', 'title', '{Conversation}', 'completed', 'c', 'https://example.invalid', 'chat_completions', 'm', 'k', '[]', 1);
            INSERT INTO ai_request (id, task_id, started_at, finished_at, outcome, output_chars) VALUES ('r1', 't1', 1, 2, 'completed', 3);
            INSERT INTO resource (id, workspace_id, conversation_id, origin, name, media_type, byte_size, sha256, trusted, created_at)
            VALUES ('doc', '{Workspace}', '{Conversation}', 'paste', NULL, 'text/plain', 6, '{Hash}', 1, 1),
                   ('ext', '{Workspace}', '{Conversation}', 'file', 'a.txt', 'text/plain', 6, '{Hash}', 0, 1),
                   ('pic', '{Workspace}', NULL, 'file', 'a.png', 'image/png', 6, '{ImageHash}', 1, 1);
            INSERT INTO resource_derivative (resource_id, kind, producer, text, created_at) VALUES ('doc', 'text', 'plain-text/1', '全文', 1);
            COMMIT;
            """);
    }
}
