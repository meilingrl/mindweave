using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Runs;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// A checkpoint writes only the blocks that changed since the last one (performance-9): while an answer streams, that is the
/// text block being written. What is stored is the same as when every block is written anew.
/// </summary>
public class CheckpointWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ToolCallBlock Call = new("c1", "search_summaries", """{"query":"x"}""", ToolRisk.LocalRead, "Searched summaries");
    private static readonly ToolResultBlock Result = new("c1", ToolResultStatus.Succeeded, new string('r', 2000), null, "found 3");

    [Fact]
    public async Task A_checkpoint_rewrites_only_the_block_being_written()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, answer) = await StoreGuardTests.SeedStreamingAsync(h);
        await RecordBlockWritesAsync(h);

        Assert.True(await h.Store.CheckpointAsync(answer, [Call, Result, new TextBlock("a")], 1, Ct));
        await ClearWritesAsync(h);

        Assert.True(await h.Store.CheckpointAsync(answer, [Call, Result, new TextBlock("ab")], 2, Ct));
        Assert.Equal(["update 2"], await WritesAsync(h));
        await ClearWritesAsync(h);

        // New blocks after the text are added; the ones before are left as they are.
        var call2 = Call with { CallId = "c2" };
        var result2 = Result with { CallId = "c2" };
        Assert.True(await h.Store.CheckpointAsync(answer, [Call, Result, new TextBlock("ab"), call2, result2, new TextBlock("c")], 3, Ct));
        Assert.Equal(["insert 3", "insert 4", "insert 5"], await WritesAsync(h));
        await ClearWritesAsync(h);

        // Nothing changed: nothing is written.
        Assert.True(await h.Store.CheckpointAsync(answer, [Call, Result, new TextBlock("ab"), call2, result2, new TextBlock("c")], 4, Ct));
        Assert.Empty(await WritesAsync(h));

        // Fewer blocks than stored: the rest go.
        Assert.True(await h.Store.CheckpointAsync(answer, [Call, Result, new TextBlock("abc")], 5, Ct));
        Assert.Equal(["update 2", "delete 3", "delete 4", "delete 5"], await WritesAsync(h));

        var stored = await h.GetMessageAsync(answer);
        Assert.Equal<ContentBlock>([Call, Result, new TextBlock("abc")], stored!.Blocks);
    }

    [Fact]
    public async Task What_checkpoints_leave_is_what_one_full_write_stores()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, checkpointed) = await StoreGuardTests.SeedStreamingAsync(h);
        var (task, written) = await StoreGuardTests.SeedStreamingAsync(h);

        var citation = new CitationBlock(1, "Page", new Uri("https://example.com/a"), DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), null, 0, 4);
        var counts = new ActivityCountBlock(1, 2, 3, 4, 5);
        var absent = new AbsentAttachmentBlock("notes.txt");
        IReadOnlyList<ContentBlock>[] steps =
        [
            [new TextBlock("")],
            [new TextBlock("Let me look.")],
            [new TextBlock("Let me look."), Call],
            [new TextBlock("Let me look."), Call, Result],
            [new TextBlock("Let me look."), Call, Result, new TextBlock("It")],
            [new TextBlock("Let me look."), Call, Result, new TextBlock("It says [1].")],
            [counts, absent, new TextBlock("Let me look."), Call, Result, new TextBlock("It says [1]."), citation],
        ];
        for (var i = 0; i < steps.Length; i++)
        {
            Assert.True(await h.Store.CheckpointAsync(checkpointed, steps[i], i + 1, Ct));
        }

        Assert.True(await h.Store.FinishAnswerAsync(task, written, steps[^1], 1, MessageState.Complete, null, null, DateTimeOffset.UtcNow, Ct));

        Assert.Equal(await RowsAsync(h, written), await RowsAsync(h, checkpointed));
        Assert.Equal(7, (await RowsAsync(h, checkpointed)).Count);
    }

    private static Task<int> RecordBlockWritesAsync(ChatHarness h) =>
        h.Database.WriteAsync(
            (c, t) => c.Execute(t, """
                CREATE TABLE test_block_write (n INTEGER PRIMARY KEY, what TEXT NOT NULL);
                CREATE TRIGGER test_block_inserted AFTER INSERT ON message_block BEGIN
                    INSERT INTO test_block_write (what) VALUES ('insert ' || new.ordinal); END;
                CREATE TRIGGER test_block_updated AFTER UPDATE ON message_block BEGIN
                    INSERT INTO test_block_write (what) VALUES ('update ' || new.ordinal); END;
                CREATE TRIGGER test_block_deleted AFTER DELETE ON message_block BEGIN
                    INSERT INTO test_block_write (what) VALUES ('delete ' || old.ordinal); END;
                """),
            Ct);

    private static Task<int> ClearWritesAsync(ChatHarness h) =>
        h.Database.WriteAsync((c, t) => c.Execute(t, "DELETE FROM test_block_write;"), Ct);

    private static Task<List<string>> WritesAsync(ChatHarness h) =>
        h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT what FROM test_block_write ORDER BY n;");
                using var r = cmd.ExecuteReader();
                var all = new List<string>();
                while (r.Read())
                {
                    all.Add(r.GetString(0));
                }

                return all;
            },
            Ct);

    private static Task<List<string>> RowsAsync(ChatHarness h, MessageId message) =>
        h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null,
                    "SELECT ordinal, kind, text, data, resource_id FROM message_block WHERE message_id = $id ORDER BY ordinal;",
                    ("$id", message.ToString()));
                using var r = cmd.ExecuteReader();
                var all = new List<string>();
                while (r.Read())
                {
                    all.Add(string.Join('|', Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "<null>" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
                }

                return all;
            },
            Ct);
}
