using System.Diagnostics;
using System.Threading.Channels;
using Mindweave.Application.Chat;
using Mindweave.Infrastructure.Runtime;
using Mindweave.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace Mindweave.Tests;

public static class ProcessProbe
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("generation-checkpoint" or "migration" or "instance"))
            return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);
        var paths = AppPaths.ForTrack("codex-steady", args[1]);
        if (args[0] == "generation-checkpoint")
        {
            await using var store = await SqliteWorkspaceStore.OpenAsync(paths);
            var conversation = await store.CreateConversationAsync((await store.CreateAsync("crash probe")).Id);
            await store.SaveConfigurationAsync(new(Guid.NewGuid(), Guid.NewGuid(), "probe", "https://example.invalid/v1", "v1", "model-a", 32768, 2048));
            var transport = new GenerationCoordinatorTests.ControlledTransport();
            await using var coordinator = new GenerationCoordinator(store, transport, new GenerationCoordinatorTests.MemoryVault());
            await coordinator.InitializeAsync();
            var changed = Channel.CreateUnbounded<bool>();
            coordinator.Changed += () => changed.Writer.TryWrite(true);
            var id = await coordinator.SendAsync(conversation.Id, "probe question");
            var call = await transport.NextAsync();
            call.Emit("saved checkpoint");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while ((await store.ReadMessagesAsync(conversation.Id)).Last().Body != "saved checkpoint")
                await changed.Reader.ReadAsync(deadline.Token);
            Console.WriteLine($"CHECKPOINT:{conversation.Id}:{id}");
            await Console.Out.FlushAsync();
            // Parent kills this process with the transport still open; no orderly Dispose/flush runs.
            await new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            return 25;
        }
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
