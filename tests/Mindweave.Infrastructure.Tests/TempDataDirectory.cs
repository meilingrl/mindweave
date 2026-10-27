using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

namespace Mindweave.Infrastructure.Tests;

/// <summary>An isolated data directory under the system temp folder, removed after the test.</summary>
public sealed class TempDataDirectory : IDisposable
{
    public TempDataDirectory()
    {
        Paths = new AppPaths("test", Path.Combine(Path.GetTempPath(), "mindweave-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Paths.DataDirectory);
    }

    public AppPaths Paths { get; }

    public SqliteDatabase Database(MigrationCatalog? catalog = null, TimeProvider? time = null) =>
        new(Paths, catalog ?? MigrationCatalog.Default, time ?? TimeProvider.System, NullLogger<SqliteDatabase>.Instance);

    /// <summary>Opens the database file directly, bypassing the application, for assertions and setup.</summary>
    public SqliteConnection Raw() => SqliteConnections.OpenReadWrite(Paths.DatabasePath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Paths.DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
