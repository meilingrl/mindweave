using Microsoft.Data.Sqlite;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Faults;

/// <summary>
/// Real storage faults on the application's own writer connection (no store wrappers): <c>max_page_count</c> is a
/// per-connection limit, so setting it through a write makes every later write that must grow the file fail with
/// <c>SQLITE_FULL</c>, exactly as a full disk does.
/// </summary>
internal static class StorageFaults
{
    /// <summary>SQLite's own default upper bound, restored when the "disk" is freed.</summary>
    private const long Unlimited = 4294967294;

    /// <summary>
    /// From now on the file can grow by at most <paramref name="headroomPages"/> pages (4 KiB each): writes that need
    /// more fail as on a full disk. Small updates in place still succeed, as they do on a real full disk.
    /// </summary>
    public static Task FillDiskAsync(SqliteDatabase database, int headroomPages = 0) =>
        database.WriteAsync(
            (c, t) =>
            {
                // Free pages inside the file are used before it grows; take them up so the headroom is exact.
                if (c.Scalar<long>(t, "PRAGMA freelist_count;") is var free and > 0)
                {
                    c.Execute(t, $"INSERT OR REPLACE INTO app_state (key, value) VALUES ('test-filler', hex(zeroblob({free * 4096 / 2})));");
                }

                Assert.Equal(0, c.Scalar<long>(t, "PRAGMA freelist_count;"));
                var pages = c.Scalar<long>(t, "PRAGMA page_count;") + headroomPages;
                Assert.Equal(pages, c.Scalar<long>(t, $"PRAGMA max_page_count = {pages};"));
                return pages;
            },
            CancellationToken.None);

    public static Task FreeDiskAsync(SqliteDatabase database) =>
        database.WriteAsync((c, t) => c.Scalar<long>(t, $"PRAGMA max_page_count = {Unlimited};"), CancellationToken.None);

    /// <summary>Row counts of every application table, to show that a failed write left nothing behind.</summary>
    public static Task<IReadOnlyDictionary<string, long>> CountRowsAsync(SqliteDatabase database) =>
        database.ReadAsync<IReadOnlyDictionary<string, long>>(
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

                return tables.ToDictionary(t => t, t => c.Scalar<long>(null, $"SELECT count(*) FROM \"{t}\";"));
            },
            CancellationToken.None);

    /// <summary>Full integrity and foreign key checks on a separate connection.</summary>
    public static Task AssertIntactAsync(SqliteDatabase database) =>
        database.ReadAsync(
            c =>
            {
                Assert.Equal("ok", c.Scalar<string>(null, "PRAGMA integrity_check;"));
                using var command = c.Command(null, "PRAGMA foreign_key_check;");
                using var reader = command.ExecuteReader();
                Assert.False(reader.Read(), reader.HasRows ? $"dangling reference in {reader.GetString(0)}" : null);
                return 0;
            },
            CancellationToken.None);

    /// <summary>Holds the database's write lock from another connection, as another program would.</summary>
    public static SqliteConnection HoldWriteLock(string databasePath)
    {
        var connection = SqliteConnections.OpenReadWrite(databasePath);
        SqliteConnections.Execute(connection, "BEGIN IMMEDIATE;");
        return connection;
    }
}
