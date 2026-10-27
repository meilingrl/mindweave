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

    /// <summary>
    /// Waits until the database file's first page can be read. On Windows a killed process's mandatory file locks
    /// outlive it briefly; tests that read the file directly (bypassing the application's own retry) wait here.
    /// </summary>
    public async Task WaitUntilReadableAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var buffer = new byte[4096];
        while (true)
        {
            try
            {
                await using var file = new FileStream(Paths.DatabasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _ = await file.ReadAsync(buffer);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }
        }
    }

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
