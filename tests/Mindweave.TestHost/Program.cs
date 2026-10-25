// Child process for fault-injection tests: lets a test kill a real process at a known point.
using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Infrastructure.Hosting;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Infrastructure.Sqlite.Migrations;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: Mindweave.TestHost <hold-lock|hang-in-migration|commit-then-hang> <data-dir>");
    return 64;
}

var paths = new AppPaths("test", args[1]);
switch (args[0])
{
    case "hold-lock":
    {
        await using var instance = SingleInstance.TryAcquire(paths);
        if (instance is null)
        {
            Console.WriteLine("busy");
            return 1;
        }

        instance.StartListening(() => Console.WriteLine("activated"));
        Console.WriteLine("ready");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    case "hang-in-migration":
    {
        // Schema 2 inserts a row, then blocks forever inside the still-open upgrade transaction.
        var catalog = new MigrationCatalog(
        [
            .. MigrationCatalog.Default.Migrations,
            new Migration(MigrationCatalog.Default.LatestVersion + 1, "hang", (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO app_state (key, value) VALUES ('half-migrated', 'yes');";
                command.ExecuteNonQuery();
                Console.WriteLine("in-migration");
                Thread.Sleep(Timeout.Infinite);
            }),
        ]);
        await using var database = new SqliteDatabase(paths, catalog, TimeProvider.System, NullLogger<SqliteDatabase>.Instance);
        await database.OpenAsync(CancellationToken.None);
        return 2;
    }

    case "commit-then-hang":
    {
        var database = new SqliteDatabase(paths, MigrationCatalog.Default, TimeProvider.System, NullLogger<SqliteDatabase>.Instance);
        var result = await database.OpenAsync(CancellationToken.None);
        if (!result.IsReady)
        {
            Console.WriteLine($"not-ready {result.Status}");
            return 3;
        }

        await database.WriteAsync(
            (connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO app_state (key, value) VALUES ('committed', 'yes');";
                return command.ExecuteNonQuery();
            },
            CancellationToken.None);
        Console.WriteLine("committed");
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown command {args[0]}");
        return 64;
}
