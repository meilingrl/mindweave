using Microsoft.Extensions.Logging.Abstractions;
using Mindweave.Application;
using Mindweave.Application.Startup;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>Handoff S1 acceptance: kill the process mid-answer; on restart it is interrupted and not re-sent.</summary>
public sealed class KillDuringStreamTests : IDisposable
{
    private readonly TempDataDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Killing_the_process_mid_answer_leaves_an_interrupted_answer_with_its_last_checkpoint()
    {
        using var child = ChildProcess.Start("stream-then-hang", _dir.Paths.DataDirectory);
        var line = await child.WaitForLineAsync(TimeSpan.FromSeconds(30));
        var answerId = MessageId.Parse(line["streaming ".Length..]);

        await child.KillAsync();

        await using var database = _dir.Database();
        var store = new SqliteConversationStore(database);
        var startup = new StartupCoordinator(database, new SqliteWorkspaceStore(database), store, new SystemClock(), new SqliteLocationStore(database, new SystemClock()), NullLogger<StartupCoordinator>.Instance, new NoResources());
        var outcome = await startup.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(outcome is StartupOutcome.ReadyToWork, outcome is StartupOutcome.NeedsRecovery r ? $"{r.Result.Status}: {r.Result.ErrorDetail}" : outcome.ToString());

        var answer = await database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT m.state, b.text FROM message m JOIN message_block b ON b.message_id = m.id AND b.ordinal = 0 WHERE m.id = $id;", ("$id", answerId.ToString()));
                using var r = cmd.ExecuteReader();
                r.Read();
                return (State: r.GetString(0), Body: r.GetString(1));
            },
            TestContext.Current.CancellationToken);
        Assert.Equal("interrupted", answer.State);
        Assert.NotEmpty(answer.Body);
        Assert.All(answer.Body, ch => Assert.Equal('字', ch));
        var unfinished = await database.ReadAsync(
            c => c.Scalar<long>(null, "SELECT count(*) FROM generation_task WHERE state IN ('queued', 'running');"),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, unfinished);
    }
}
