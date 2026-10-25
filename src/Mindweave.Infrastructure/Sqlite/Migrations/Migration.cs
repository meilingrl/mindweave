using Microsoft.Data.Sqlite;

namespace Mindweave.Infrastructure.Sqlite.Migrations;

/// <summary>
/// One forward-only schema step. It runs inside the shared upgrade transaction, so it may only use
/// statements that are valid inside a transaction (no VACUUM, no journal_mode changes).
/// </summary>
public sealed record Migration(int Version, string Name, Action<SqliteConnection, SqliteTransaction> Apply)
{
    public static Migration Sql(int version, string name, string sql) =>
        new(version, name, (connection, transaction) => SqliteConnections.Execute(connection, sql, transaction));
}

/// <summary>An ordered, gap-free list of migrations. The last version is the schema this build writes.</summary>
public sealed class MigrationCatalog
{
    public MigrationCatalog(IEnumerable<Migration> migrations)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        Migrations = migrations.OrderBy(m => m.Version).ToArray();
        for (var i = 0; i < Migrations.Count; i++)
        {
            if (Migrations[i].Version != i + 1)
            {
                throw new ArgumentException($"Migration versions must be 1..n without gaps; found {Migrations[i].Version} at position {i + 1}.", nameof(migrations));
            }
        }
    }

    public static MigrationCatalog Default { get; } = new(SchemaMigrations.All);

    public IReadOnlyList<Migration> Migrations { get; }

    public int LatestVersion => Migrations.Count;
}
