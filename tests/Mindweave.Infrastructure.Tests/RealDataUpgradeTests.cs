using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application.Startup;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>
/// Upgrades a COPY of real data through the application's own open path (2026-11-13, migration v12 trial). Opt-in:
/// set MINDWEAVE_UPGRADE_TRIAL_DIR to a data directory holding a copy of mindweave.db (for example made with
/// <c>sqlite3 &lt;daily db&gt; ".backup '&lt;dir&gt;/mindweave.db'"</c>). Never point it at the real data directory.
/// </summary>
public class RealDataUpgradeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Category", "Trial")]
    public async Task A_copy_of_real_data_upgrades_with_every_row_kept_and_passes_its_checks()
    {
        var dir = Environment.GetEnvironmentVariable("MINDWEAVE_UPGRADE_TRIAL_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(dir), "Set MINDWEAVE_UPGRADE_TRIAL_DIR to a directory holding a copy of the data.");
        var paths = new AppPaths("trial", dir!);
        Assert.True(File.Exists(paths.DatabasePath), paths.DatabasePath);
        var before = Counts(paths.DatabasePath, out var fromVersion);
        var branches = Branches(paths.DatabasePath);

        await using (var database = new SqliteDatabase(paths, MigrationCatalog.Default, TimeProvider.System, NullLogger<SqliteDatabase>.Instance, new SqliteDatabaseOptions()))
        {
            var opened = await database.OpenAsync(Ct);
            Assert.True(opened.IsReady, $"{opened.Status}: {opened.ErrorDetail}");
            if (fromVersion < MigrationCatalog.Default.LatestVersion)
            {
                Assert.NotNull(opened.BackupPath);
                Assert.True(File.Exists(opened.BackupPath));
            }
        }

        SqliteConnection.ClearAllPools();
        var after = Counts(paths.DatabasePath, out var toVersion);
        Assert.Equal(MigrationCatalog.Default.LatestVersion, toVersion);
        Assert.Equal(before, new SortedDictionary<string, long>(after.Where(p => before.ContainsKey(p.Key)).ToDictionary(), StringComparer.Ordinal)); // A migration may add tables.
        Assert.Equal(branches, Branches(paths.DatabasePath));
        using var raw = SqliteConnections.OpenReadWrite(paths.DatabasePath);
        Assert.Equal("ok", SqliteConnections.Scalar<string>(raw, "PRAGMA integrity_check;"));
        Assert.Equal(0, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(6, SqliteConnections.Scalar<long>(raw, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name IN ('message_by_supersedes', 'reading_position_by_message', 'generation_task_by_target', 'summary_node_by_current', 'summary_node_by_pending', 'summary_edit_draft_by_baseline');"));
        TestContext.Current.TestOutputHelper?.WriteLine($"v{fromVersion} -> v{toVersion}; rows per table kept: {string.Join(", ", after.Select(p => $"{p.Key} {p.Value}"))}");
    }

    /// <summary>Every branch's source and frozen ranges, value for value (v19 rebuilds both tables).</summary>
    private static (string? Sources, string? Ranges) Branches(string path)
    {
        using var raw = SqliteConnections.OpenReadWrite(path);
        return (
            SqliteConnections.Scalar<string>(raw, """
                SELECT group_concat(line, char(10)) FROM (
                    SELECT rowid || '|' || child_id || '|' || parent_id || '|' || source_message_id || '|' || source_block || '|' || quote || '|' || anchor || '|' || anchor_version || '|' || render_profile || '|' || created_at AS line
                    FROM branch_source WHERE anchor IS NOT NULL ORDER BY rowid);
                """),
            SqliteConnections.Scalar<string>(raw, "SELECT group_concat(child_id || '|' || ancestor_id || '|' || depth || '|' || max_seq, char(10)) FROM (SELECT * FROM branch_context_range ORDER BY child_id, depth);"));
    }

    private static SortedDictionary<string, long> Counts(string path, out long version)
    {
        using var raw = SqliteConnections.OpenReadWrite(path);
        version = SqliteConnections.Scalar<long>(raw, "PRAGMA user_version;");
        var tables = new List<string>();
        using (var command = raw.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        return new SortedDictionary<string, long>(tables.ToDictionary(t => t, t => SqliteConnections.Scalar<long>(raw, $"SELECT count(*) FROM \"{t}\";")), StringComparer.Ordinal);
    }
}
