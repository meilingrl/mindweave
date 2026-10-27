using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Xunit;

namespace Mindweave.Tests;

public sealed class SummaryMigrationTests
{
    internal static string Snapshot(string path, IEnumerable<string>? names = null)
    {
        using var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"); c.Open();
        using var t = c.BeginTransaction(deferred: true);
        var tables = names?.ToArray();
        if (tables is null)
        {
            using var list = c.CreateCommand(); list.Transaction = t;
            list.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = list.ExecuteReader(); var result = new List<string>(); while (r.Read()) result.Add(r.GetString(0)); tables = result.ToArray();
        }
        var data = new SortedDictionary<string, string[]>();
        foreach (var table in tables)
        {
            using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = $"SELECT * FROM \"{table}\"";
            using var r = q.ExecuteReader(); var rows = new List<string>();
            while (r.Read()) { var values = new object[r.FieldCount]; r.GetValues(values); rows.Add(JsonSerializer.Serialize(values)); }
            data[table] = rows.Order(StringComparer.Ordinal).ToArray();
        }
        return JsonSerializer.Serialize(data);
    }
    internal static string[] Tables(string snapshot) => JsonSerializer.Deserialize<Dictionary<string, string[]>>(snapshot)!.Keys.ToArray();
    internal static AppPaths Golden(TestDirectory dir, string mode)
    {
        var p = AppPaths.ForTrack("codex-steady", dir.Path); Directory.CreateDirectory(p.DataDirectory);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "summary-schema5-format6", mode, "schema5.db"), p.DatabasePath); return p;
    }
    internal static async Task UpgradePreservingOldRows(AppPaths paths)
    {
        var before = Snapshot(paths.DatabasePath); var names = Tables(before);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(paths)) { }
        Assert.Equal(before, Snapshot(paths.DatabasePath, names));
    }
    [Fact]
    public async Task Fresh_database_is_schema6_with_full_future_contract_tables()
    {
        using var dir = new TestDirectory(); var p = AppPaths.ForTrack("codex-steady", dir.Path);
        await using var store = await SqliteWorkspaceStore.OpenAsync(p);
        Assert.Equal(6L, ContextStorageTests.Sql(p, "PRAGMA user_version"));
        foreach (var table in new[] { "summary_node", "summary_version_identity", "summary_version", "summary_version_source", "summary_version_message", "knowledge_summary_source", "summary_read_state", "summary_edit_draft", "knowledge_summary_job", "knowledge_summary_prepared", "knowledge_summary_material", "summary_selection", "node_naming_group", "node_naming_job", "node_naming_member" })
            Assert.Equal(1L, ContextStorageTests.Sql(p, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name", ("$name", table)));
    }
    [Theory]
    [InlineData("default")]
    [InlineData("disabled")]
    public async Task Upgrade_preserves_every_old_value_and_backup_and_does_not_rewrite_on_read(string mode)
    {
        using var dir = new TestDirectory(); var p = Golden(dir, mode); var old = Snapshot(p.DatabasePath);
        await UpgradePreservingOldRows(p);
        Assert.Equal(6L, ContextStorageTests.Sql(p, "PRAGMA user_version"));
        Assert.Equal(old, Snapshot(Assert.Single(Directory.GetFiles(p.BackupDirectory, "*.db"))));
        var baseline = File.ReadAllBytes(p.DatabasePath);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(p)) { await store.ListAsync(); }
        Assert.Equal(baseline, File.ReadAllBytes(p.DatabasePath));
    }

    [Fact]
    public void Migrations_one_through_five_remain_exact_bytes()
    {
        string[] hashes = ["5F5DDCD36B3510E3BDBEA4FBDF5AD8CB4ECA463B8754FFEB7BEC78A718502CB1", "B9E27CB98682C8196425DCFC1015FFAB8683018C8684BE85DDBF99A110BBFA82", "4B17BD52BFFDBE085968E528019E6B96AC938F3BACD36B7E2342A54A09397942", "9D514D3699E4B92B5543FFDCD83CDD20504C15147BB7E9CF7307286DD296C2A3", "6B6D14EAB3F6AAE80F150517CDA634DA1BA05129F9525CE04FD3CC0A64F11856"];
        Assert.Equal(hashes, DatabaseSchema.Migrations.Take(5).Select(m => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(m.Sql)))));
    }
    [Fact]
    public async Task Late_ddl_fault_rolls_back_schema6_and_keeps_complete_schema5_backup()
    {
        using var dir = new TestDirectory(); var p = Golden(dir, "default"); var before = Snapshot(p.DatabasePath);
        var migrations = DatabaseSchema.Migrations.Take(5).Append(new SchemaMigration(6, DatabaseSchema.Migrations[5].Sql + "UPDATE workspace SET name='wrong'; SELECT * FROM synthetic_missing_table;")).ToArray();
        await Assert.ThrowsAsync<SqliteException>(() => SqliteWorkspaceStore.OpenAsync(p, migrations));
        Assert.Equal(5L, ContextStorageTests.Sql(p, "PRAGMA user_version")); Assert.Equal(before, Snapshot(p.DatabasePath));
        Assert.Equal(before, Snapshot(Assert.Single(Directory.GetFiles(p.BackupDirectory, "*.db"))));
        Assert.Equal(0L, ContextStorageTests.Sql(p, "SELECT count(*) FROM sqlite_master WHERE name='summary_node'"));
    }
    [Fact]
    public async Task Backup_failure_prevents_schema6_and_newer_schema_is_not_modified()
    {
        using var dir = new TestDirectory(); var p = Golden(dir, "disabled"); var before = Snapshot(p.DatabasePath);
        File.WriteAllText(p.BackupDirectory, "synthetic backup path fault");
        await Assert.ThrowsAsync<IOException>(() => SqliteWorkspaceStore.OpenAsync(p));
        Assert.Equal(before, Snapshot(p.DatabasePath)); Assert.Equal(5L, ContextStorageTests.Sql(p, "PRAGMA user_version"));
        ContextStorageTests.Sql(p, "PRAGMA user_version=7"); var bytes = File.ReadAllBytes(p.DatabasePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SqliteWorkspaceStore.OpenAsync(p)); Assert.Equal(bytes, File.ReadAllBytes(p.DatabasePath));
    }
    [Fact]
    public async Task Killing_schema6_migration_process_preserves_schema5_commits_and_backup()
    {
        using var dir = new TestDirectory(); var p = Golden(dir, "default"); var before = Snapshot(p.DatabasePath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); using var child = ProcessProbe.Start("summary-migration", dir.Path);
        try
        {
            Assert.Equal("SUMMARY-MIGRATING", await child.StandardOutput.ReadLineAsync(timeout.Token)); child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token);
            // Writable open permits SQLite's hot-journal recovery before read-only snapshot assertions.
            using (var c = new SqliteConnection($"Data Source={p.DatabasePath};Pooling=False")) { c.Open(); using var q = c.CreateCommand(); q.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", q.ExecuteScalar()); }
            Assert.Equal(before, Snapshot(p.DatabasePath)); Assert.Equal(5L, ContextStorageTests.Sql(p, "PRAGMA user_version"));
            Assert.Equal(before, Snapshot(Assert.Single(Directory.GetFiles(p.BackupDirectory, "*.db"))));
        }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(timeout.Token); } }
    }
    [Theory]
    [InlineData("default")]
    [InlineData("disabled")]
    public async Task Format6_golden_preserves_full_requests_cache_sources_prepared_and_readonly_bytes(string mode)
    {
        using var dir = new TestDirectory(); var p = Golden(dir, mode);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "summary-schema5-format6", mode, "manifest.json")));
        var golden = manifest.RootElement; T Get<T>(string name) => golden.GetProperty(name).Deserialize<T>()!;
        await UpgradePreservingOldRows(p); var before = File.ReadAllBytes(p.DatabasePath);
        using var handler = new ContextThinkingTransportTests.CaptureHandler(); using var client = new HttpClient(handler); var transport = new Mindweave.Infrastructure.AI.ChatCompletionsTransport(client);
        await using (var store = await SqliteWorkspaceStore.OpenAsync(p))
        {
            var config = Get<Mindweave.Core.Chat.AiConfiguration>("Configuration"); Assert.Equal(config, await store.LoadActiveConfigurationAsync());
            var taskId = Get<Guid>("TaskId"); var plan = (await store.LoadContextPlanAsync(taskId))!; Assert.Equal(6, plan.FormatVersion);
            var attempts = Get<Mindweave.Core.Chat.ContextCompressionAttempt[]>("Attempts");
            var requests = Get<Mindweave.Application.Chat.ChatRequest[]>("SummaryRequests"); var summaries = Get<Mindweave.Core.Chat.CompletedContextSummary[]>("Summaries");
            for (var i = 0; i < attempts.Length; i++)
            {
                var a = (await store.LoadCompressionAttemptAsync(attempts[i].Id))!;
                Assert.Equal(JsonSerializer.Serialize(attempts[i]), JsonSerializer.Serialize(a));
                Assert.Equal(JsonSerializer.Serialize(summaries[i]), JsonSerializer.Serialize(await store.LoadCompletedSummaryAsync(taskId, a.Chunk.CacheKey)));
                var request = Mindweave.Application.Chat.ReplyContextPlanner.CreateSummaryRequest(a.SourceConversationId, a.Chunk, await store.ReadMessagesAsync(a.SourceConversationId), a.Configuration);
                Assert.Equal(JsonSerializer.Serialize(requests[i]), JsonSerializer.Serialize(request)); await ContextThinkingTransportTests.Send(transport, request);
            }
            var prepared = (await store.LoadPreparedReplyRequestAsync(taskId))!;
            Assert.Equal(JsonSerializer.Serialize(Get<Mindweave.Application.Chat.PreparedReplyRequest>("Prepared")), JsonSerializer.Serialize(prepared));
            Assert.Equal(Get<string>("PreparedHash"), ContextStorageTests.Sql(p, "SELECT request_hash FROM prepared_reply_request WHERE task_id=$id", ("$id", taskId)));
            await ContextThinkingTransportTests.Send(transport, prepared.Request);
            Assert.Equal("synthetic preserved draft", (await store.LoadDraftAsync(Get<Guid>("SiblingId"))).Text);
            Assert.Equal(0L, ContextStorageTests.Sql(p, "SELECT count(*) FROM pragma_foreign_key_check"));
        }
        Assert.Equal(Get<string[]>("PhysicalBodies"), handler.Bodies); Assert.Equal(before, File.ReadAllBytes(p.DatabasePath));
    }
}
