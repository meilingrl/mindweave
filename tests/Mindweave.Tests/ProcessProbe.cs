using System.Diagnostics;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Mindweave.Tests;

public static class ProcessProbe
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) return 20;
        var paths = AppPaths.ForTrack("codex-steady", args[1]);
        if (args[0] == "migration")
        {
            // Use rollback journaling to observe the real migration transaction before killing it.
            using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=DELETE";
                command.ExecuteNonQuery();
            }
            var opening = SqliteWorkspaceStore.OpenAsync(paths, [new(1, ""), new(2, """
                UPDATE workspace SET name='uncommitted';
                WITH RECURSIVE forever(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM forever WHERE x<1000000000000) SELECT sum(x) FROM forever;
                """)]);
            while (!File.Exists(paths.DatabasePath + "-journal"))
            {
                if (opening.IsCompleted) { await opening; return 23; }
                await Task.Delay(10);
            }
            Console.WriteLine("MIGRATING");
            await opening;
            return 24;
        }
        if (args[0] != "instance") return 20;
        using var instance = SingleInstance.TryAcquire(paths);
        if (instance is not null) return 21;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await SingleInstance.NotifyExistingAsync(paths, timeout.Token);
        return 0;
    }

    public static Process Start(string mode, string root)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(typeof(ProcessProbe).Assembly.Location);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(root);
        return Process.Start(info) ?? throw new InvalidOperationException("Cannot start test subprocess.");
    }
}
